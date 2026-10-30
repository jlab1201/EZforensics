# EZ Forensics (formerly EZ Hub) — shared contracts (read before coding)

- **EzDialog (replaces every MessageBox)**: `Views/EzDialog` (borderless, themed with DynamicResource, severity-coloured border and glyph, draggable header, owner-centred). Always opened through `Services/Dialogs`: `Show(title, message, severity, details, buttons, primary, cancel, owner)` returns the clicked button index (Esc returns `cancel`, default = last button), plus `Info` / `Warn` / `Error` and `Confirm(title, message, yes, no, severity, details, defaultNo, owner)`. Technical text (paths, errors, file lists) goes in `details` (collapsed "Details" expander). AutomationIds: `EzDialog` (window, HelpText = severity), `EzDialog.Icon`, `EzDialog.Title`, `EzDialog.Message`, `EzDialog.Details` (expander), `EzDialog.DetailsText`, `EzDialog.Button.1` .. `EzDialog.Button.3` (left to right; the primary one uses AccentButton). Only `App.ShowStartupError` may still call `MessageBox` (when theme resources are not loaded).
- **Event log re-run prompt**: "Event logs already parsed" / "This host already has event log output." with buttons Replace (`EzDialog.Button.1`), Keep both (`.2`), Cancel (`.3`); the existing files are listed under Details.

Solution: `EzForensics.sln`
- `src/EzForensics.Core` (net8.0, no WPF). Packages: Sylvan.Data.Csv, YamlDotNet, Registry (Eric Zimmerman, MIT; offline hives for Core/SystemInfo).
- `src/EzForensics.App` (net8.0-windows, WPF).
- `tests/EzForensics.Core.Tests` (xUnit + FluentAssertions 6).
- `tests/EzForensics.FakeTool` (console exe).

Root namespace = `EzForensics.Core.<Folder>`. Use `System.Text.Json` (camelCase, enums as strings) for every JSON file.
Dev copy of the real EZ tools (for checking `--help` output, not shipped): `.devtools\` in the repo root, laid out the way Get-ZimmermanTools leaves it (`.devtools\net9\...`).

## Folder ownership (parallel agents: touch ONLY your folders)
| Area | Owner | Folders |
|---|---|---|
| Catalog + commands | Agent A | `Core/Catalog`, `Core/Commands`, `Core/Resources/tools`, `Tests/Catalog*`, `Tests/Commands*` |
| Execution + tools + settings | Agent B | `Core/Execution`, `Core/Tools`, `Core/Settings`, `tests/EzForensics.FakeTool`, `Tests/Execution*`, `Tests/Tools*` |
| Triage + results | Agent C | `Core/Triage`, `Core/Results`, `Core/Resources/triage`, `tests/TestData`, `Tests/Triage*`, `Tests/Results*` |
| WPF app | Agent D (later) | `src/EzForensics.App` |
| Super timeline | Timeline | `Core/Timeline`, `Core/Resources/timeline`, `tests/TestData/timeline`, `Tests/Timeline*` |
| plaso engine | Timeline | `Core/Plaso`, `Tests/Plaso*`, `tests/EzForensics.FakeTool/FakePlasoTool.cs` |
| Zeek + shared runtimes | Network | `Core/Runtimes`, `Core/Network`, `Core/Zeek`, `Core/Triage/NetworkTriage.cs`, `tests/TestData/zeek`, `Tests/Zeek*`, `tests/EzForensics.FakeTool/FakeZeekTool.cs` |
| Case layout v2 (hosts, naming, provenance) | Case layout | `Core/Case`, the "Case layout v2" section below; shares `Core/Execution/OutputNaming.cs` (rename table), `Core/Triage/*Planner/Scanner/Triage`, `Core/Timeline/TimelineMappings.cs`, `Tests/CaseLayout*` |
| Memory (Volatility 3) | Memory | `Core/Memory`, `Core/Resources/memory`, `Core/Triage/MemoryTriage.cs`, `Core/SystemInfo/MemoryDetailsProvider.cs`, `tests/EzForensics.FakeTool/FakeVolatilityTool.cs`, `Tests/Memory*`, `Tests/Volatility*` |
| Archived evidence | Archives | `Core/Archives`, `Tests/Archive*`, `tests/TestData/archives` |
| RITA engine | RITA | `Core/Rita`, `tests/EzForensics.FakeTool/FakeRitaTool.cs`, `Tests/Rita*`; appends the Rita rows of `Core/Execution/OutputNaming.cs` and the Rita settings |

Shared files (`EzForensics.Core.csproj`, the test csproj): append to them only. Add an `<EmbeddedResource Include="Resources\**\*.json" />` item if it is not already there.
If `dotnet build` fails because another agent holds a file lock, just retry the build. Don't edit another agent's files to work around it.

## Cross-area types (exact signatures)

```csharp
// Core/Catalog  (Agent A)
public enum ToolKind { Cli, Gui }
public enum ParamType { File, Folder, FileOrFolder, OutputDir, Enum, Bool, String, Int, Date, Multi }
public sealed class ToolDefinition { string Id; string DisplayName; string Category; string Description; ToolKind Kind;
  string Exe; string Zip; List<string> Builds /* "net9","net4" */; bool Exclusive; bool RequiresAdmin;
  List<ParameterDef> Parameters; List<ParamGroup> Groups; }
public static class ToolCatalog { static IReadOnlyList<ToolDefinition> LoadEmbedded(); static ToolDefinition? Find(string id); }

// Core/Commands  (Agent A)
public sealed record BuiltCommand(IReadOnlyList<string> Args, IReadOnlyList<string> Errors) { bool IsValid; string ToDisplayString(string exePath); string ToPowerShell(string exePath); }
public static class CommandBuilder { static BuiltCommand Build(ToolDefinition def, IReadOnlyDictionary<string,string?> values); }

// Core/Tools  (Agent B)
public enum BuildFlavor { Net9, Net4 }
public sealed record InstalledTool(string ExeName, string Path, BuildFlavor Flavor, string? Version);
public interface IToolLocator { void Rescan(string toolsRoot); InstalledTool? Resolve(string exeName, BuildFlavor? preferred = null);
  IReadOnlyList<InstalledTool> All { get; } }
public static class RuntimeProbe { static bool HasNet9(); }   // cached

// Core/Execution  (Agent B)
public sealed record JobSpec(string ToolId, string ExeName, IReadOnlyList<string> Args, string OutputDir, string Label, string Category, bool Exclusive = false);
public enum JobState { Queued, Running, Succeeded, Failed, TimedOut, Cancelled, Skipped }
public sealed class Job : INotifyPropertyChanged { Guid Id; JobSpec Spec; JobState State; DateTime? StartUtc, EndUtc; int? ExitCode;
  string? ExePath; string? LogPath; event Action<Job,string>? Output; void Cancel(); }
public sealed class JobQueue { JobQueue(IToolLocator locator, int maxParallel, TimeSpan timeout);
  Job Enqueue(JobSpec spec, string caseDir); Task WhenAllAsync(); void CancelAll(); ObservableCollection<Job> Jobs; /* raise events on caller's SynchronizationContext */ }
public sealed class RunManifest  /* written to <caseDir>\EzForensics_manifest_<utcstamp>.json (older cases: EzHub_manifest_*.json, still read), atomically rewritten after every job */

// Core/Triage  (Agent C)
public sealed record DetectedArtifact(string RuleId, string Label, string ToolId, string Category, string MatchedPath, bool PerMatch,
  int MatchCount = 1, string SystemRoot = "", bool RequiresAdmin = false, bool DefaultSelected = true, DiscoveredSystem? System = null)
  { string Host /* System?.HostName ?? "" */; AcquisitionKind? Acquisition; }
public static class TriagePlanner { static IReadOnlyList<JobSpec> Plan(IReadOnlyList<DetectedArtifact> found, string caseDir, string systemRoot);
  static IReadOnlyList<PlannedJob> PlanJobs(found, rules, caseDir[, systemRoot /* fallback for hits without SystemRoot */]); }  // layout v2: see "Case layout v2"

// Core/Results  (Agent C)
public sealed class CsvIndex : IDisposable { static Task<CsvIndex> OpenAsync(string path, IProgress<long>? rowsIndexed, CancellationToken ct);
  IReadOnlyList<string> Headers; long RowCount; string[] ReadRow(long index); }
public static class FilterEngine { static Task<int[]> FilterAsync(CsvIndex idx, FilterSpec spec, IProgress<double>? p, CancellationToken ct);
  static Task<int[]> SortAsync(CsvIndex idx, int[] rows, string column, bool descending, CancellationToken ct = default, IProgress<double>? p = null); }
public enum ColumnFilterMode { Contains, Equals, Regex, NotContains, NotEquals, BeginsWith, EndsWith, Like /* * and ? */, IsEmpty, IsNotEmpty,
  Greater, Less, Between /* numeric or chronological when the operand parses as a number/date, else ordinal */ }   // all case-insensitive
public sealed record ColumnFilter(string Column, ColumnFilterMode Mode, string Value) { string? Value2 /* Between upper bound */; }
public enum FlagFilterMode { All, Flagged, Unflagged }
public sealed class FilterSpec { string? GlobalText; List<ColumnFilter> ColumnFilters; FilterNode? Expression; string? DateColumn; DateTime? DateFrom, DateTo;
  FlagFilterMode Flag; IReadOnlySet<long>? FlaggedRows /* snapshot of original row indices */; bool IsEmpty;
  IReadOnlyDictionary<long,FlagColor>? FlagColorRows /* row -> colour snapshot; used instead of FlaggedRows when set */;
  IReadOnlySet<FlagColor>? FlagColors /* Flag == Flagged: only these colours (null/empty = any) */; bool FlagMatches(long row); int[] FlaggedCandidates(long rowCount);
  FilterGroup BuildExpression() /* ColumnFilters + Expression as one And group */; string ToDisplayString(); }
  // Everything is ANDed: global search, column filters, Expression, date range, flag filter. Flag == Flagged evaluates only the flagged rows (no full scan).
  // ToDisplayString: "[Flag]", "Not [Flag]", "[Flag] In ('Red', 'Blue')".
public static class CsvExporter { static Task ExportAsync(CsvIndex idx, IReadOnlyList<int> rows, string outPath, IProgress<double>? p = null, CancellationToken ct = default,
  ExportExtraColumn? extra = null /* written as the first column */); }
public sealed record ExportExtraColumn(string Header, Func<int,string> Value) { static ExportExtraColumn FlagColumn(IReadOnlyDictionary<long,FlagColor>, IReadOnlyList<string> headers)
  /* "Flag" (or "EzForensics_Flag" when the CSV has a Flag column) = colour name, "" when not flagged */; }
// Filter expression tree (Timeline Explorer / DevExpress style). Nodes on unknown columns, empty groups and conditions without a value place no constraint.
public abstract class FilterNode { bool IsEmpty; FilterNode Clone(); string ToDisplayString(); bool References(string column); }
public sealed class FilterGroup : FilterNode { FilterGroupKind Kind /* And|Or */; bool Negate; List<FilterNode> Children;
  static FilterGroup And(params FilterNode[]), Or(params FilterNode[]), Not(FilterNode); bool RemoveColumn(string column); }
public sealed class FilterCondition : FilterNode { string Column; ColumnFilterMode Op; string Value; string? Value2; static bool NeedsValue(ColumnFilterMode op);
  static Regex LikeToRegex(string pattern); }
public sealed class FilterValueSet : FilterNode { string Column; HashSet<string> Values /* case-insensitive */; bool IncludeBlanks; }
public sealed class FilterDateSet : FilterNode { string Column; HashSet<DateOnly> Days /* UTC day of the parsed timestamp */; bool IncludeBlanks /* empty or unparseable */; }
  // ToDisplayString: [Source] = 'MFT' And Contains([Message], 'powershell') And Not [Flag]; <>, StartsWith(), EndsWith(), Like, IsNullOrEmpty(), Regex(),
  // >, <, Between(a, b), [C] In ('a', 'b'), [C] InDays(#2024-01-01#..#2024-01-03#), Not (...), Contains(*, 'x') for the global search.
public static class DistinctValueCounter { const int DefaultMaxValues = 10_000, DefaultMaxTracked = 250_000;
  static Task<DistinctValueResult> CountAsync(CsvIndex idx, string column, int maxValues = 10_000, string? search = null, IProgress<double>? p = null,
    CancellationToken ct = default, int maxTracked = 250_000);   // DistinctValueResult { Values (DistinctValue(Value, Count), most frequent first), BlankCount,
                                                                 //   NonBlankCount, RowsScanned, DistinctCount, Truncated, CountsApproximate }
  static Task<DateBucketResult> CountDaysAsync(CsvIndex idx, string column, IProgress<double>? p = null, CancellationToken ct = default); // Days (DayCount), BlankCount
  static bool LooksLikeDateColumn(CsvIndex idx, string column, int sample = 200); }
public sealed record CsvFingerprint(long Size, DateTime LastWriteUtc, string HeadSha256 /* first 64 KB */) { static CsvFingerprint Compute(string path); }
public sealed class RowFlagStore : IDisposable {   // flags keyed on the original file row index; thread safe
  static RowFlagStore Open(string csvPath, string? fallbackDir = null, TimeSpan? debounce = null /* 500 ms */);
  static string SidecarPathFor(string csv) /* Foo.csv -> Foo.ezflags.json */; static string FallbackPathFor(string csv, string dir) /* <dir>\<sha256(path)>.json */;
  static string DefaultFallbackDir /* %LOCALAPPDATA%\EzForensicsData\flags */; string SidecarPath; bool UsingFallback; string? StaleWarning; Exception? LastSaveError;
  int Count; bool IsFlagged(long row); bool Set(long row, bool on); bool Toggle(long row); int SetMany(IEnumerable<long> rows, bool on); void Clear();
  HashSet<long> Snapshot(); long[] ToSortedArray(IReadOnlyCollection<FlagColor>? colors = null); void Flush(); event Action? Changed;
  // colours (one per row): Set/Toggle/SetMany flag with DefaultColor and keep the colour of rows already flagged
  FlagColor DefaultColor /* Red */; FlagColor? GetColor(long row); int SetColor(IEnumerable<long> rows, FlagColor? color /* null = clear */);
  int CountOf(FlagColor c); Dictionary<FlagColor,int> Counts() /* every palette colour */; Dictionary<long,FlagColor> SnapshotColors(); }
public enum FlagColor { Red = 1, Orange, Yellow, Green, Blue, Purple }
public static class FlagCatalog { const string Informational/Interesting/Suspicious/Malicious/Benign (stable ids); List<FlagDefinition> Defaults() /* in this order = Ctrl+1..5 */; string? MigrateLegacy(string?) /* red->malicious, orange/yellow->suspicious, green->benign, blue->informational, purple->interesting */; Normalize; Resolve; Next(list, id?); } public sealed class FlagDefinition { string Id; string Label; string Color /* #RRGGBB */; }  // RowFlagStore stores flag ids (sidecar version 3, v1/v2 migrated on load); AppSettings.Flags (ordered list) + FlagLastId replace FlagColorNames/FlagLastColor; CsvExporter.FlagColumn writes the label. Options ids: `Settings.Flag.<id>.Label|Color|Preset|Up|Down|Remove`, `Settings.Flag.Add`, `Settings.Flag.Reset`. Flag menu items: `Flag.<id>`; filter check boxes `Results.FlagFilter.<id>`. Ctrl+1..9 = Nth flag.
  static bool TryParse(string? name, out FlagColor) /* names only */; }
  // Sidecar JSON (RowFlagFile, version 2): { version: 2, csvFile, csvSize, csvLastWriteUtc, csvHeadSha256, rows: [all flagged, ascending],
  //   colors: { "red": [..], "blue": [..] } }, written atomically (tmp + rename) after the debounce. Version 1 (rows only) loads as Red; rows missing
  //   from colors or under an unknown colour name are Red. A v1 file is rewritten as v2 on the next change only.
  // A fingerprint mismatch drops the flags (StaleWarning). A write refused in the CSV folder switches to the fallback path.

// Core/Timeline  (super timeline; mappings embedded from Resources/timeline/mappings.json)
public sealed class TimelineSourceType { string Id; string Source; string Artifact; string? Label; List<string> FileNamePatterns, PathPatterns,
  RequiredHeaders /* "A|B" = either */; List<TimelineTimestampColumn> Timestamps /* {Column, Description} */; bool AutoTimestamps, Ignore,
  DefaultIncluded; string? Message, Path /* templates */; List<string> UserColumns, UserFromPathColumns; string? UserFromFileName, ArtifactFromFileName;
  // layout v2 (mappings.json version 3): List<string> ArtifactIds /* manifest artifact ids */, OutputNames /* regexes on v2 file names */;
  //   string? OutputArtifactFromFileName, OutputUserFromFileName /* user = sanitised qualifier: last resort after columns and paths */; bool IsHeaderOnly;
  bool Matches(...) /* MatchesLegacy || MatchesOutputName */; bool MatchesLegacy(fileName, relPath, headers); bool MatchesOutputName(fileName, headers);
  bool MatchesArtifactId(artifactId, headers); bool HeadersMatch(headers); string ArtifactFor(fileName); string? UserForFileName(fileName); string? UserForOutputName(fileName); }
public sealed class TimelineMappings { const string GenericId = "generic"; List<TimelineSourceType> SourceTypes; static TimelineMappings LoadEmbedded();
  static TimelineMappings Load(Stream json); TimelineSourceType? Find(string id);
  TimelineSourceType? Match(string fileName, string relPath, IReadOnlyCollection<string> headers, string? artifactId = null);
  /* order: (1) manifest artifactId in ArtifactIds, (2) header-only types, (3) OutputNames, (4) legacy fileNamePatterns/pathPatterns; headers checked at every step */ }
public sealed class TimelineTemplate { static TimelineTemplate Compile(string template, IReadOnlyList<string> headers); string Render(Func<int,string> cell); }
  // {Col}, {A|B} first non-empty, {Col:name} file name only, [ ... ] optional group dropped when all its columns are empty
public enum TimestampParseResult { Empty, NullDate, Ok, Invalid }
public static class TimelineTimestamp { const string OutputFormat = "yyyy-MM-dd HH:mm:ss.fffffff";
  static TimestampParseResult TryParse(string? s, out DateTime utc, bool strict = false); static string Format(DateTime utc); }
public sealed record TimelineInput(string Path, string RelativePath, TimelineSourceType? SourceType, IReadOnlyList<string> Headers, long Size,
  long EstimatedRows, string? SkipReason, string? Host = null, string? ArtifactId = null, string? ToolId = null) { bool IsGeneric; string SourceTypeId; }
public sealed record TimelineSourceGroup(string Id, string Label, string Source, bool IsGeneric, bool DefaultIncluded, int Files, long EstimatedRows, long Bytes);
public static class TimelineScanner { const string TimelineFolderName = "Timeline", OutputPrefix = "SuperTimeline_";
  static IReadOnlyList<TimelineInput> Scan(string caseDir, TimelineMappings? m = null, CancellationToken ct = default, IEnumerable<string>? extraExcludedDirs = null);
  static bool IsExcluded(string relativePath); static IReadOnlyList<TimelineSourceGroup> Summarize(IEnumerable<TimelineInput> inputs, TimelineMappings? m = null); }
public sealed class TimelineOptions { string CaseDir; ISet<string>? IncludeSourceTypes /* null = DefaultIncluded types */; bool IncludeGeneric = true;
  DateTime? FromUtc, ToUtc /* inclusive */; string? OutputPath /* default <case>\Timeline\SuperTimeline_EZ.csv, see "Output file names" */; int ChunkRows = 250_000;
  long ChunkBytes; int MergeFanIn = 64; string? TempDir; bool ComputeHashes = true, WriteManifest = true; TimelineMappings? Mappings;
  bool TimestampOutputName /* old SuperTimeline_<utc>.csv default name */; const string DefaultOutputName = "SuperTimeline_EZ.csv";
  static string DefaultOutputPath(string caseDir, DateTime? utcNow = null, bool timestamped = false); }
public enum TimelinePhase { Scanning, Reading, Merging, Writing, Done }
public sealed record TimelineProgress(TimelinePhase Phase, string? CurrentFile, int FilesDone, int FilesTotal, long RowsRead, long EventsCollected, long RowsWritten);
public sealed class TimelineSummary { string OutputPath; string? ManifestPath; long RowsWritten; TimeSpan Elapsed; long UnparseableTimestamps,
  NullTimestamps, OutOfRangeTimestamps; int ChunkFiles; List<TimelineSourceStats> Sources; List<TimelineFileStats> Files; List<TimelineSkippedFile> Skipped; }
public sealed class TimelineManifest  /* <output without .csv>.manifest.json: options, input SHA-256s + row counts, skipped files, output SHA-256 */
public static class SuperTimelineBuilder { static readonly string[] OutputHeaders /* DateTimeUtc,TimestampDescription,MACB,Host,Source,Artifact,Message,User,Path,SourceFile,SourceRow;
    Host = manifest host / layout v2 host folder of the input, "" for layout v1 inputs */;
  static TimelineSummary Build(TimelineOptions o, IProgress<TimelineProgress>? p = null, CancellationToken ct = default);
  static Task<TimelineSummary> BuildAsync(TimelineOptions o, IProgress<TimelineProgress>? p = null, CancellationToken ct = default); }
// MACB (plaso style, 4 chars): TimelineTimestampColumn { string? Macb; string EffectiveMacb /* explicit, else inferred from Description */ };
//   mappings.json "macb" per timestamp: M... modified/last write (also deleted, like plaso), .A.. accessed/last run/start/interacted,
//   ..C. $MFT record / metadata change (USN update), ...B created/birth (also evtx TimeCreated = plaso "Creation Time"), .... other.
//   Auto/generic columns: TimelineMacb.Infer(header). TimelineOptions.CombineMacb (default false; UI default true): same SourceFile+SourceRow
//   +timestamp -> one row, MACB = letter union (TimelineMacb.Merge), descriptions joined with "; ".
public static class TimelineMacb { const string Unknown = "...."; static string Normalize(string?); static string Merge(string?, string?); static string Infer(string?); }

// Core/Plaso  (plaso / log2timeline super-timeline engine; tested against plaso 20260720)
public enum PlasoEngineKind { Executable, Python, Docker }   public enum PlasoOrigin { Configured, ManagedVenv, Path, Docker }
public sealed record PlasoCommand(string Exe, IReadOnlyList<string> PrefixArgs);   // e.g. (python.exe, ["-m","plaso.scripts.log2timeline"])
public sealed record PlasoInstall(PlasoOrigin Origin, PlasoEngineKind Kind, PlasoCommand Log2Timeline, PlasoCommand Psort, string? Version, string Location, string DockerImage);
public interface IPlasoProbe { bool FileExists(string); bool DirectoryExists(string); string? FindOnPath(string); string? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout); }
public sealed class PlasoLocator { PlasoLocator(IPlasoProbe? probe = null, string? managedVenv = null);
  PlasoDiscovery Discover(string? configuredPath, bool includeDocker = true);  // configured path -> %LOCALAPPDATA%\EzForensicsData\plaso\venv -> PATH -> Docker; first --version that answers
  IReadOnlyList<PythonInfo> FindPythons(); PythonInfo? FindSupportedPython() /* py -3, python on PATH, >= 3.10, Store stub ignored */;
  (IReadOnlyList<string> CreateVenv, string VenvPython, IReadOnlyList<string> PipInstall) InstallCommands(); }
public sealed class PlasoRunOptions { string Source /* raw evidence folder or image */, CaseDir, Stamp; string? Parsers; bool TimezoneUtc = true; int? Workers;
  PlasoVssMode Vss = None; bool Unattended = true; PlasoOutputFormat Format = Dynamic; DateTime? FromUtc, ToUtc;
  string StorageFile /* <TimelineDir>\plaso\<stamp>.plaso */, OutputCsv /* <TimelineDir>\SuperTimeline_plaso_<stamp>.csv */;
  string? OutputDir /* null = <case>\Timeline; Ingest: <case>\<Host>\Timeline */; JobProvenance? Provenance /* on both jobs */; string TimelineDir; }
public static class PlasoCommandBuilder { const string DynamicFields = "datetime,timestamp_desc,macb,source,source_long,message,parser,display_name,tag";
  static PlasoInvocation BuildLog2Timeline(PlasoInstall, PlasoRunOptions); static PlasoInvocation BuildPsort(PlasoInstall, PlasoRunOptions);
  static string? BuildDateFilter(DateTime? from, DateTime? to) /* date >= DATETIME('...') AND date <= DATETIME('...') */; }
public sealed record PlasoInvocation(string Exe, IReadOnlyList<string> Args, string? ContainerName, IReadOnlyList<string>? CancelCommand /* docker stop */);
public sealed class PlasoStatusParser { PlasoStatusParser(PlasoPhase); PlasoStatus? Feed(string line); }  // --status_view linear
public static class PlasoTimelineRunner { static Task<PlasoRunResult> RunAsync(JobQueue q, PlasoInstall i, PlasoRunOptions o, Action<Job>? onJob, Action<PlasoStatus>? onStatus, CancellationToken ct);
  static Task WaitAsync(Job job); }  // two JobQueue jobs: plaso-log2timeline then plaso-psort
// Tool Manager entry (install / update / uninstall), appended:
public static class PlasoLinks { const string Docs, UsersGuide, DockerDocs, GitHub, Releases, PyPI, PyPIJson, DockerHub, PythonDownloads, PythonNuGet; }
public sealed record PlasoRelease(string Version, DateTime? UploadedUtc, long? WheelSize, string? RequiresPython);
public static class PlasoReleaseCheck { static PlasoRelease? ParsePyPiJson(string json); static Task<PlasoRelease> GetLatestAsync(HttpClient, TimeSpan? timeout = 15 s, CancellationToken);
  static int Compare(string? a, string? b) /* date versions */; static bool IsNewer(string? latest, string? installed); }
public enum PlasoToolStatus { Detecting, Installed, UpdateAvailable, NotInstalled, PythonMissing }   PlasoToolState.Evaluate(...), Label(status)
public sealed record PrivatePythonSpec(string Version, string Url, string Sha512Base64, long Size);
public static class PrivatePython { static PrivatePythonSpec Default /* nuget.org python 3.12.10, pinned SHA-512 */; static bool IsOfficialUrl(string);
  static bool VerifySha512(string file, string b64); static int ExtractTools(string nupkg, string dir); static Task<string> ProvisionAsync(HttpClient, spec, dir, log, progress, ct); }
public static class PlasoSetup { CreateVenvStep / PipStep(update) / DockerPullStep -> PlasoSetupStep; static Task<Job> RunStepAsync(JobQueue, step, outDir, onJob, ct);
  static IReadOnlyList<string> Uninstall(PlasoLocator) /* venv + private python only */; static (int DownloadMB, int DiskMB) Estimate(bool needsPython); }
// Several sources (appended): PlasoRunOptions { IReadOnlyList<string> AdditionalSources = []; IReadOnlyList<string> AllSources /* Source + AdditionalSources */;
//   string Log2TimelineLogFor(int index) /* 0 = Log2TimelineLog, then <stamp>.log2timeline-<n>.log.gz */; const string SourceSeparator = "; ";
//   static string JoinSources(IEnumerable<string>); static IReadOnlyList<string> ParseSources(string? text, Func<string,bool>? exists = null)
//   /* whole text when it exists or has no ';', else the ';'-separated parts */ }. PlasoCommandBuilder.BuildLog2Timeline(install, o, int sourceIndex) and
//   PlasoTimelineRunner.Log2TimelineSpec(install, o, int sourceIndex) (label "... (i/n)"; Docker container "-l2t<n>"). RunAsync runs one log2timeline job per source,
//   sequentially, all with the same --storage_file: plaso opens an existing storage file for appending (one session per run; verified in plaso 20260720
//   cli/extraction_tool.py "Appending to an already existing storage file." + storage/sqlite/writer.py Open counts the existing containers), then one psort exports
//   every session. ExtractedEvents / Warnings are summed; a failed source stops the build ("log2timeline (source i of n): ...").
//   UI: SuperTimelineViewModel.SetDefaultPlasoSources(roots) shows "a; b" in the plaso source box (the Ingest page passes this ingest's extracted archive roots,
//   never their common parent <case>\.ezstaging\extracted, which can hold other ingests' copies); the box accepts several ';'-separated sources.
// PlasoLocator additions: MaxPython (3.13; PythonInfo.IsSupported = 3.10..3.13), PrivatePythonDir/Exe (FindPythons lists it first), UpdateCommand(), IsManaged(install),
//   UninstallTargets(), static PinfoCommand(install), ProbeVersion(PlasoCommand). ManagedRoot honours EZFORENSICS_PLASO_ROOT (or the old EZHUB_PLASO_ROOT).

// Core/Execution (appended, follow-ups): Task Job.Recorded /* completes after the job is final AND its run-manifest record was written (or the write failed);
//   State turns final before that, so anything that reads the manifest from disk after a job awaits Recorded */;
//   JobQueue.SetSource(string? path, IReadOnlyList<string>? roots); IReadOnlyList<string>? JobQueue.SourceRoots (setting SourcePath alone clears the roots);
//   RunManifest.SourceRoots (List<string>?, "sourceRoots", omitted when null): the folders the jobs read when they differ from sourcePath (archive extraction roots).
// Core/Execution additions (appended optional JobSpec parameters; existing callers unchanged)
//   JobSpec(..., string? ExePath = null /* skip the tool locator */, TimeSpan? Timeout = null /* per-job; InfiniteTimeSpan = none */,
//           IReadOnlyList<string>? CancelCommand = null /* run after a cancel/timeout kill, e.g. docker stop */, string? WorkingDir = null, string? ExeVersion = null)
// Core/Settings additions: string? PlasoPath; AutoBuildEngine AutoBuildEngine { Off, EzCsv, Plaso } = EzCsv (used only while AutoBuildSuperTimeline is on).
```

```csharp
// Core/Settings (append-only): AppSettings.NavPinned (bool, default false: the drawer starts unpinned), CellViewerWidth / CellViewerHeight (double).
// EZFORENSICS_SETTINGS=<path> (or the old EZHUB_SETTINGS) makes the app use another settings file (portable use, UI tests).
// Appearance (append-only): double UiZoom (1.0, clamped 0.5–2.5); string UiFontFamily ("Segoe UI Variable Text, Segoe UI"); double UiFontSize (13);
//   string MonoFontFamily ("Cascadia Mono, Consolas, Courier New"); double MonoFontSize (12). Sizes are clamped to 8–32, empty families fall back to the defaults.
public static class UiZoom { const double Min = 0.5, Max = 2.5, Step = 0.1, Default = 1.0; static double Clamp(double); static double In(double); static double Out(double);
  static string Format(double) /* "110%" */; }
public static class UiFonts { DefaultUiFamily, DefaultMonoFamily, DefaultUiSize, DefaultMonoSize, MinSize, MaxSize; static double ClampSize(double, double fallback);
  static string Family(string?, string fallback); static double GridRowHeight(double monoSize) /* ceil(2 * size): 24 at 12 */; }

// Core/Results
public static class CaseTreeOrder { static bool IsSuperTimeline(string name, bool isDirectory) /* "Timeline" folder or SuperTimeline_*.csv */;
  static bool IsListedFile(string fileName) /* *.csv only; never *.ezstats.json / *.ezflags.json */;
  static int Rank(string name, bool isDirectory); static List<T> Sort<T>(IEnumerable<T>, Func<T,string> name, Func<T,bool> isDirectory, Func<T,DateTime>? lastWriteUtc = null);
  /* Timeline folder, then SuperTimeline_*.csv newest first (last-write UTC, name as tie-breaker; by name descending without a time),
     then folders, then files (by name) */
  static List<T> SortBy<T>(IReadOnlyList<T> defaultOrder, Func<T,string> name, Func<T,bool> isDirectory, Func<T,IComparable?>? key, bool descending,
    bool pinSuperTimeline) /* header sort: key null = default order (desc = reversed); null keys last; ties keep default order */; }
// Tree columns (Earliest | Latest | Events | Size). FileStats: one streaming pass, rows counted exactly like CsvIndex; timestamp column =
//   TimestampColumnGuesser.Guess(headers, csvPath) (same as the grid): artifact-aware (Core/Advisories Identify) so embedded / reliable columns win over
//   Source* file-system times (Prefetch: LastRun; LNK: TargetModified, TargetCreated; Jump Lists: LastModified, CreationTime, Target*); generic
//   order puts SourceCreated / SourceModified last. Empty / unparseable / 1601-01-01 / 0001-01-01 cells ignored; timestamps outside
//   TimestampPlausibility ([1980-01-01, now + 1 year]) are excluded from Earliest / Latest and counted in ImplausibleCount (tooltip "N implausible
//   timestamps ignored"); grids and the super timeline keep every row. Schema version 2 (older sidecars are recomputed).
public sealed class FileStats { const int CurrentSchemaVersion = 2; int SchemaVersion; long ImplausibleCount; long Size; DateTime LastWriteUtc; bool IsCsv; string? TimestampColumn;
  long? Events /* null for non-CSV */; DateTime? Earliest, Latest /* UTC, null = "-" */; bool Matches(long size, DateTime lastWriteUtc); StatsSummary Summary; }
public readonly record struct StatsSummary(DateTime? Earliest, DateTime? Latest, long? Events, long Size, long Implausible = 0);
public static class TimestampPlausibility { static readonly DateTime MinUtc /* 1980-01-01 */; static DateTime MaxUtc(DateTime nowUtc) /* +1 year */; static bool IsPlausible(DateTime utc, DateTime nowUtc); static string Describe(long n); }
public static class FileStatsCalculator { static bool IsCsvPath(string); static FileStats Compute(string path, CancellationToken ct = default); }
public static class FileStatsAggregator { static StatsSummary Combine(IEnumerable<StatsSummary>) /* min / max / sums; null when no item has a value */; }
public static class FileStatsFormat { const string None = "-", Pending = "…", DefaultTimestampFormat = "yyyy-MM-dd HH:mm:ss";
  static string Timestamp(DateTime? utc, string? format /* AppSettings.DateFormat; empty = default */); static string Events(long?, IFormatProvider? = null) /* N0 */;
  static string Size(long); }
public sealed class FileStatsCache /* thread safe */ { const string SidecarSuffix = ".ezstats.json"; FileStatsCache(string? fallbackDir = null, TimeSpan? settleTime = null /* 5 s */);
  static string DefaultFallbackDir /* %LOCALAPPDATA%\EzForensicsData\cache */; static string SidecarPathFor(string) /* Foo.csv -> Foo.csv.ezstats.json */;
  static string FallbackPathFor(string path, string dir) /* <dir>\<sha256(path)>.ezstats.json */; FileStats? TryGet(string path) /* memory, sidecar, fallback; valid only while
  size + last-write UTC + schema match */; FileStats GetOrCompute(string path, CancellationToken ct[, out bool fromCache]); string? Store(string path, FileStats)
  /* null = memory only (file written < settleTime ago, or nothing writable) */; void Forget(string path); }
// Core/Settings (append-only): AppSettings.ResultsTreeColumns (ResultsTreeColumns: bool ShowEarliest/ShowLatest/ShowEvents/ShowSize (true);
//   double NameWidth 280, EarliestWidth 140, LatestWidth 140, EventsWidth 80, SizeWidth 72; MinWidth 36, MinNameWidth 120;
//   appended: bool ShowTool (true), double ToolWidth 110).
```

App (WPF) resources set at runtime by `Services/Appearance`: `Ui.Scale` (frozen ScaleTransform, replaced on every zoom change; use it as the
`LayoutTransform` of a window's root, or `Appearance.ApplyTo(popupChild)` for a Popup), `Ui.TextFormatting` (Display at 100 %, Ideal when scaled),
`Font.UI`, `FontSize.Body`, `Font.Mono`, `FontSize.Mono`, `Grid.RowHeight`. Reference them with `DynamicResource`. New windows call `Appearance.Attach(this)`
for the zoom shortcuts (Ctrl+= / Ctrl++ / Ctrl+- / Ctrl+0, Ctrl+wheel). UI automation ids: `StatusBar.Zoom`, `StatusBar.ZoomIn`, `StatusBar.ZoomOut`,
`StatusBar.ZoomReset`, `Results.Tree.ExpandAll`, `Results.Tree.CollapseAll`, `Settings.UiFontFamily`, `Settings.UiFontSize`, `Settings.MonoFontFamily`,
`Settings.MonoFontSize`, `Settings.StripTimestampPrefix`; Results tree items expose the node name as their UIA Name, the full path as HelpText and
`Earliest: …; Latest: …; Events: …; Size: …` as ItemStatus. Tree columns: `Results.Tree.Header` (header row), `Results.Tree.Header.Name|Tool|Earliest|Latest|Events|Size`
(click = sort), `Results.Tree.Columns` (picker button), menu `Results.Tree.ColumnsMenu` with checkable `Results.Tree.Columns.<column>`, `Results.Tree.StatsProgress`.
Column picker items are MenuItems with IsCheckable + StaysOpenOnClick (Toggle and Invoke patterns; the global MenuItem template now draws a check box for
checkable items, an Icon, InputGestureText and sub-menus), plus `Results.Tree.Columns.ShowAll` and `Results.Tree.Columns.Reset`; Name is checked and disabled.

```csharp
// Results flag colours (App). Toolbar dropdown `Results.FlagFilter` (ToggleButton; ItemStatus = button text) opens popup `Results.FlagFilter.Popup` with check boxes
//   `Results.FlagFilter.All`, `Results.ShowOnlyFlagged` (= "All flagged"; the old toolbar toggle's id), `Results.FlagFilter.Unflagged`,
//   `Results.FlagFilter.<Colour>` (Name = "Red · meaning", ItemStatus = count) and buttons `Results.FlagFilter.<Colour>.Only`.
// Flag cell `Results.FlagCell`: HelpText = IsFlagged (as before), ItemStatus = colour ("Red" / "Red · Malicious" / "").
// Context menu: `Results.Menu.Flag` (sub-menu) with `Results.Menu.Flag.<Colour>` and `Results.Menu.Flag.Clear`; `Results.Menu.FlagSelected` /
//   `Results.Menu.UnflagSelected` unchanged. Keys in the grid: Ctrl+1..6 colour, Ctrl+0 clear (Appearance.HandlesCtrlZero = true on the grid; text boxes excluded).
// Core/Settings (append-only): bool FlagRowTint (true); flag names/colours live in AppSettings.Flags (ordered list) + FlagLastId (see FlagCatalog).
//   Settings ids: `Settings.FlagRowTint`, `Settings.Flag.<id>.*` (see FlagCatalog).
// Theme brushes: Brush.FlagTint.<Colour> (row tint); style FlagSwatch (Border, Tag = colour name) in Controls.xaml.

// Core/SystemInfo (computer details; offline registry via Eric Zimmerman's Registry NuGet package, MIT)
public sealed record DetailField(string Category, string Field, string Value /* "-" when unknown */, string Source /* hive\key\value */);
public sealed class SystemDetails { string SystemRoot, Title, Host /* case host folder; "" for legacy reports */, Summary /* "HOST · OS (build) · ip, ip +n" */; List<DetailField> Fields; List<HiveInfo> Hives; List<string> Notes; }
public sealed class ComputerDetailsReport { int SchemaVersion = 1; string Source; DateTime GeneratedUtc; string Generator; List<SystemDetails> Systems; List<string> Notes; }
public sealed class HiveInfo { string Name, Path; long Size; string? Sha256; bool Dirty; List<string> TransactionLogs; bool LogsReplayed; string? Note; }
public interface IRegistryHive : IDisposable { HiveInfo Info; RegKey? GetKey(string path); }   public interface IHiveOpener { IRegistryHive Open(string hivePath, string name); }
public abstract class RegKey { string Name; DateTime? LastWriteUtc; IReadOnlyList<RegKey> SubKeys; IReadOnlyList<RegValue> Values; RegValue? Value(string); RegKey? SubKey(string); }
public sealed class RegValue { string Name; uint Type /* raw, e.g. RID for SAM Names */; byte[] Raw; string? AsString(); string[] AsMulti(); long? AsNumber(); }
public sealed class ZimmermanHiveOpener : IHiveOpener   // copies hive + .LOG1/.LOG2 to a temp folder, replays dirty hives in memory, never writes the evidence
public sealed class InMemoryHive : IRegistryHive        // tests: Key(path).With(RegValue.Sz/Dword/Qword/Multi/Bin(...))
public interface IComputerDetailsProvider { void Contribute(SystemDetails s, string systemRoot, CancellationToken ct); }   // hook (e.g. Volatility later)
public sealed class ComputerDetailsCollector { ComputerDetailsCollector(IHiveOpener? opener = null); List<IComputerDetailsProvider> Providers;
  static IReadOnlyList<string> FindSystemRoots(string source) /* SourceLayoutDetector roots that hold SYSTEM or SOFTWARE */;
  ComputerDetailsReport Collect(string source, Action<string>? log = null, CancellationToken ct = default); SystemDetails CollectSystem(string root, ...);
  static SystemDetails Extract(string root, IRegistryHive? system, IRegistryHive? software, IRegistryHive? sam, FileFacts? files = null) /* pure */; }
public static class ComputerDetailsStore { const string FolderName = "SystemInfo", JsonName = "ComputerDetails.json", CsvName = "ComputerDetails.csv",
  ToolId = "ezforensics-computer-details"; static (string Json, string Csv) Write(report, outDir) /* atomic */;
  static ComputerDetailsReport? Load(string caseDir) /* = TryLoad: merges legacy <case>\SystemInfo + every <case>\<Host>\SystemInfo; each SystemDetails.Host set */;
  static string FolderFor(caseDir) /* legacy <case>\SystemInfo */; HostFolderFor(caseDir, host) /* <case>\<HostFolder>\SystemInfo */; HostJsonPathFor/HostCsvPathFor(caseDir, host);
  static string CsvPathFor(caseDir) /* legacy csv if it exists, else the first host's, else the legacy path */;
  static string? FindSourceFromManifests(string caseDir) /* first of FindSourcesFromManifests */;
  static IReadOnlyList<string> FindSourcesFromManifests(string caseDir) /* newest manifest with existing folders: its sourceRoots, else sourcePath when it is a folder
    (a sourcePath that is an archive / image file is never returned) */;
  sealed record ManifestSystem(string RootPath, string Host); static IReadOnlyList<ManifestSystem> FindSystemsFromManifests(string caseDir)
    /* sources[].rootPath + host of every manifest, newest first, one per host, existing roots only: the exact system roots the jobs read */;
  static int RunCommandLine(args, TextWriter out) /* --source <dir> --out <dir> [--host <host folder>] (sets SystemDetails.Host); 0 ok, 2 no hives, 1 error */; }
// Case layout: layout v2 case (case.json) → <case>\<Host>\SystemInfo\ComputerDetails.json/.csv, one file pair per host (single-system csv format);
//   older case → <case>\SystemInfo\ComputerDetails.json/.csv (still read; the Results page sees one merged report either way).
// App: ComputerDetailsJob.Enqueue(AppServices, IReadOnlyList<string> sources, caseDir, systems = null) (several roots: systems discovered in each, host names unique;
//   the single-source overload remains); ComputerDetailsJob.SystemsFromManifests(caseDir). The Results page / Refresh use the manifest systems first, then the
//   manifest sources, then Report.Source / a folder picker. After an ingest run the Ingest page passes this run's extracted roots (or the source folder) + checked systems.
// App: ComputerDetailsJob.Enqueue(AppServices, source, caseDir, IReadOnlyList<DiscoveredSystem>? systems = null) returns the last job queued. v2 case: one job per Windows
//   system `EzForensics.exe --computer-details --source <system root> --out <case>\<Host>\SystemInfo --host <Host>` (systems null = discovered from source);
//   older case: one job `--source <src> --out <case>\SystemInfo`.
//   (JobSpec.ExePath = the running exe, ToolId ezforensics-computer-details, category SystemInfo): logged, on the Jobs page, outputs hashed in the run manifest.
//   Queued by TriageViewModel when a run finishes (checked systems only), by the Results page for a case without a report (manifest source), and by Refresh.
// UI ids: `Results.ComputerDetails` (pane), `.Toggle`, `.Summary`, `.Status`, `.Body`, `.Refresh`, `.OpenReport`, `.CopyAll`; rows `Results.ComputerDetails.Row`
//   (Name = field, HelpText = source, ItemStatus = value); row menu `Results.ComputerDetails.Menu.CopyValue|CopyRow|CopyAll`.
```

```csharp
// Core/Execution additions (appended, existing callers unchanged)
//   JobSpec(..., Func<JobPostProcessContext, CancellationToken, Task>? PostProcess = null /* after the process exits (not on cancel/timeout),
//           before outputs are classified and hashed; exception = Failed, OCE = Cancelled */, IReadOnlyDictionary<string,string>? ManifestDetails = null)
//   JobPostProcessContext { Job Job; int ExitCode; string OutputDir; void Log(string); void SetDetail(string key, string value); }
//   Job.Details (IReadOnlyDictionary<string,string>); ManifestJob.Details (omitted when empty): e.g. zeek.version, zeek.engine, docker.repoDigest, input.sha256.
//   OutputNaming.SidecarSuffixes (".ezstats.json", ".ezflags.json"), IsEzForensicsSidecar(path): EZ Forensics sidecars are never job outputs: not in the
//   OutputSnapshot / renamer, not in the manifest's outputFiles, not counted by JobOutcomeClassifier.

// Core/Runtimes (generic, reused later by Volatility): never installs Docker or WSL (admin + consent). The only distro it creates is the private one (EzDistro).
public interface IRuntimeProbe { bool FileExists(string); bool DirectoryExists(string); string? FindOnPath(string); string? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, IReadOnlyDictionary<string,string>? env = null); }
public sealed record DockerState(string? Cli, bool EngineRunning, string? DesktopExe, string? ServerVersion);
public sealed record DockerImageInfo(string Id, IReadOnlyList<string> RepoDigests, long? Size) { string? Digest; }
public static class DockerRuntime { FindCli, FindDesktopExe, Detect(probe), InspectImage(probe, cli, image), ParseInspect, PullArgs, RemoveImageArgs, StopArgs,
  MountSpec(host, target, readOnly), NewContainerName(tool), HubTagsUrl, ParseHubTags, GetHubTagsAsync, ResolveMovingTag(tags, "lts") -> (x.y.z, digest); }
public sealed record WslDistro(string Name, bool IsDefault, string State, int Version);  public sealed record WslOsRelease(string Id, string VersionId, string? PrettyName);
public static class WslRuntime { Utf8Env /* WSL_UTF8=1 */; FindWslExe; ListDistros (docker-desktop* excluded); ParseList; ReadOsRelease; ParseOsRelease;
  ToWslPath("D:\\x") -> "/mnt/d/x" (null for UNC); Args(distro, cmd, linuxWorkDir?, asRoot) -> -d <d> [-u root] [--cd dir] -e cmd... /* -e keeps argv intact */; ShQuote;
  IsEnabled(probe, wslExe) /* wsl --status succeeds */; }
// Private EZ Forensics WSL distro (Core/Runtimes/EzDistro.cs). Name "EzForensics"; rootfs = Ubuntu Base 24.04.5 (cdimage.ubuntu.com
//   .../ubuntu-base/releases/24.04.5/release/ubuntu-base-24.04.5-base-<arch>.tar.gz, SHA-256 pinned, amd64 also size-pinned 30,028,293 B);
//   `wsl --import EzForensics <DataRoot>\wsl\EzForensics <rootfs> --version 2` (per user, NO admin; EZFORENSICS_WSL_ROOT overrides <DataRoot>\wsl);
//   when that fails (no hardware / nested virtualization) the partial install is cleaned up (unregister + folder) and the import is retried
//   once with --version 1 (logged with the WSL 2 reason; never `wsl --update` / `--set-version`). WSL 1 keeps a rootfs folder, no ext4.vhdx;
//   nothing in the distro / Zeek / RITA set-up assumes WSL 2. Setup records AppSettings.EzDistroWslVersion; EzDistroInfo.Summary shows "WSL n".
//   wsl.exe output is decoded with WslOutputEncoding (UTF-8, or UTF-16LE detected from the first bytes: older wsl.exe ignores WSL_UTF8=1) in
//   ProcessRunner, SystemRuntimeProbe and EzDistro.RunAsync; ProcessRunner also drops stray NULs from every line. A failed import's reason is
//   DescribeFailure(job.StatusReason, job log) = "Exited with code -1. wsl.exe: <last 3 meaningful output lines>".
//   first boot (root): /etc/wsl.conf (systemd off, default user root, automount /mnt/, generateHosts/ResolvConf, hostname ezforensics,
//   interop off, no Windows PATH), apt update + ca-certificates curl gnupg procps (pkill for job cancel), marker /etc/ezforensics/provisioned
//   ("ubuntu-base 24.04.5 / schema 1"), then `wsl --terminate`. Never runs, changes or unregisters any other distro.
public sealed record EzRootfsPin(Os, Version, Architecture, Url, Sha256, Bytes);  public sealed record RuntimeStep(ToolId, Exe, Args, Label, Timeout, Env) { ToJobSpec(dir); }
public sealed record EzDistroInfo(bool WslAvailable, bool Exists, int WslVersion, string? State, bool? Provisioned, string? BaseVersion, string InstallDir,
  long? DiskBytes /* ext4.vhdx */, string? WslExe) { bool Ready; string Summary; }
public sealed record WslRunResult(int ExitCode, string Output, bool Cancelled) { bool Ok; }
public static class EzDistro { const Name = "EzForensics", SchemaVersion = 1, MarkerPath; BasePackages; UbuntuBaseAmd64/Arm64; Rootfs (by OS arch);
  DownloadMB = 75, DiskMB = 250; WslRoot, InstallDir, DownloadDir, VhdxPath(); ImportArgs(dir, tar, wslVersion = 2); UnregisterArgs(); TerminateArgs();
  ImportWithFallbackAsync(import(version, ct) -> WslImportAttempt(Ok, Detail, Cancelled), cleanUp, log, ct) -> WslImportOutcome(Ok, WslVersion, Message)
  /* 2, then (not when cancelled) cleanUp + 1 */; DescribeFailure(statusReason, output); MeaningfulTail(output, max = 3);
  ScriptArgs(script) -> -d EzForensics -u root -e sh -c <LF-only script>; Lf(s); WslConf; ProvisionScript; ProbeScript; ParseProbe;
  ImportStep / ProvisionStep / UnregisterStep (RuntimeStep, ToolIds ezdistro-import|-provision|-unregister); Inspect(probe?, checkProvisioned = true)
  /* starts only EzForensics, only when checkProvisioned */; DiskBytes(); FormatBytes; IsOfficialUrl; Sha256Of; Verify(file, pin);
  DownloadRootfsAsync(http, pin, dir, progress, log, ct) /* re-uses a verified copy; mismatch -> deleted + InvalidDataException */;
  EnsureAsync(http, runStep, log, progress?, probe?, ct) -> (Ok, Message) /* download+verify, import, provision, terminate; WSL not enabled -> (false, "WSL is not ...") */;
  RemoveAsync(runStep, probe?, ct) /* wsl --unregister EzForensics + delete the folder */;
  Task<WslRunResult> RunAsync(script, ct, onLine?, wslExe?, timeout?) /* root sh -c in EzForensics; ct kills wsl.exe — stop long Linux processes with your own pkill */; }

// Core/Network
public enum PcapFormat { Pcap, PcapNanosecond, PcapNg }   public sealed record PcapFile(string Path, long Size, PcapFormat Format);
public static class PcapDetector { static bool IsCandidateName(string); static PcapFormat? FromMagic(ReadOnlySpan<byte>); static PcapFormat? Probe(string path); }
public sealed class PcapScanOptions { MaxDepth = 8; MaxEntries = 250_000; MaxProbes = 20_000; MaxResults = 1_000; ISet<string> SkipDirectories; }
public static class PcapScanner { static PcapScanResult Scan(string fileOrFolder, PcapScanOptions? o = null, CancellationToken ct = default); } // junctions not followed

// Core/Zeek (tested with zeek/zeek:lts = 9.0.0)
public enum ZeekEngine { Native, Docker, Wsl }  public enum ZeekOrigin { Configured, Path, ProgramFiles, Docker, Wsl }
public sealed record ZeekInstall(ZeekEngine Engine, ZeekOrigin Origin, string Exe /* zeek.exe|docker.exe|wsl.exe */, string ZeekPath, string? Version,
  string Location, bool HasLocalScript, string? DockerImage, DockerImageInfo? Image, string? WslDistro, string WslMountRoot = "/mnt/") { ManifestDetails(); }
public enum ZeekSetupOption { None, DockerPull, StartDockerThenPull, WslApt /* user distro, fallback; discovery no longer picks it */, EzDistroApt /* default when WSL is enabled */, Guidance }
// ZeekInstall.IsEzDistro (Wsl engine in EzDistro.Name; OriginLabel "EZ Forensics distro").
public sealed record ZeekDiscovery(ZeekInstall? Selected, Notes, DockerState Docker, DockerImageInfo? DockerImage, string? WslExe, Distros, string? WslCandidate, WslOsRelease?, ZeekSetupOption Setup);
public sealed class ZeekLocator { const DockerImage = "zeek/zeek:lts", WslZeekPath = "/opt/zeek/bin/zeek"; static string ManagedRoot /* %LOCALAPPDATA%\EzForensicsData\zeek, EZFORENSICS_ZEEK_ROOT */;
  ZeekDiscovery Discover(string? configuredPath, bool includeDocker = true, bool includeWsl = true); /* configured -> PATH/Program Files -> EzForensics distro
    (probed even when stopped) -> user distros (running/default) -> Docker; nothing found: WSL enabled -> EzDistroApt (WslCandidate = "EzForensics"),
    else DockerPull / StartDockerThenPull / Guidance */ static WslProbeOrder(distros); static IsEzDistro(WslDistro); static string? ParseVersion(string?); }
public static class ZeekCommandBuilder { const ContainerIn = "/data/in", ContainerOut = "/data/out"; static IReadOnlyList<string> ZeekArgs(pcap, loadLocal, extra) /* -C -r <pcap> [local] [extra] LogAscii::use_json=F */;
  static ZeekInvocation Build(ZeekInstall, ZeekRunOptions, string? containerName = null); }
public static class ZeekLogConverter { const DateTimeColumn = "DateTimeUtc"; static ZeekLogConversion Convert(string log, string? csv = null, CancellationToken ct = default);
  static IReadOnlyList<ZeekLogConversion> ConvertFolder(string dir, Action<string>? log = null, CancellationToken ct = default); static string FormatEpoch(string); static string Unescape(string); }
public static class ZeekJobs { const ToolId = "Zeek", Category = "Network"; static string OutputRoot(caseDir) /* <case>\Network\Zeek; in a layout v2 case (case.json) <case>\Unknown\Network */; static bool IsHostLayout(caseDir); FolderNameFor; AssignOutputDirs;
  TimeoutFor(bytes) /* 30 min + 20 min/GB */; static JobSpec CreateSpec(ZeekInstall, pcap, outDir, ZeekRunOptions? = null, string? label = null) /* PostProcess = convert + input SHA-256 */;
  static IReadOnlyList<Job> Enqueue(JobQueue, ZeekInstall, IEnumerable<string> pcaps, string caseDir, ZeekRunOptions? = null, IReadOnlyList<DiscoveredSystem>? systems = null);
  /* case without case.json: v1 <case>\Network\Zeek\<capture>\; layout v2 case: delegates to NetworkTriage.Plan → <case>\<Host or Unknown>\Network\Zeek_<Log>_<capture>.csv/.log */ }
public static class ZeekSetup { DockerPullStep, DockerRemoveStep, WslInstallStep/WslUpdateStep/WslRemoveStep(wslExe, distro) (root sh scripts, official OBS repo
  security:zeek; the same scripts for EzForensics and a user distro), RunStepAsync, StartDockerDesktopAsync; WslDownloadMB = 140, WslDiskMB = 500 (measured, Zeek 9.0.0); }
public sealed class ZeekSetupState { string? DockerImagePulled; List<string> WslDistros /* includes "EzForensics" when installed there */; static Load(root?); Save(root?); }  // Uninstall removes only these
// Tool Manager Zeek card (App): Install with EzDistroApt = EzDistro.EnsureAsync + WslInstallStep(EzForensics); "Move to EZ Forensics distro"
//   (ToolManager.Zeek.MoveToEzDistro, shown when Zeek runs in a user distro; optionally purges the managed copy there);
//   "Remove Linux environment…" (ToolManager.Zeek.RemoveEzDistro, confirmation: deletes Zeek/RITA + data inside; EzDistro.RemoveAsync).
public sealed record ZeekRelease(string? Version, DateTime? PublishedUtc, string? ImageVersion, string? ImageDigest, string? Error);
public static class ZeekReleaseCheck { ParseGitHubLatest; GetLatestAsync(http, 15 s) /* GitHub latest + Docker Hub lts */; Compare; IsNewer; }
public enum ZeekToolStatus { Detecting, Installed, UpdateAvailable, NotInstalled, NeedsRuntime }  ZeekToolState.Evaluate(...) /* Docker: digest differs */, Label(...)
public static class ZeekLinks { Website, Docs, InstallDocs, LogsDocs, GitHub, Releases, LatestReleaseApi, License, DockerHub, DockerRepo, ObsProject, ObsRepoBase, DockerDesktop(Docs), WslInstall; }

// Core/Triage
public static class NetworkTriage { const RuleId = "pcap", OutputName = "Zeek"; static IReadOnlyList<PlannedJob> Plan(pcaps, caseDir, ZeekInstall? install,
  string? sourceRoot = null, IReadOnlyList<DiscoveredSystem>? systems = null /* host = system holding the capture, else the only system, else "Unknown" */);
  static bool IsPlaceholder(JobSpec) /* planned before Zeek existed */; static JobSpec Resolve(PlannedJob, ZeekInstall) /* keeps OutputDir, Output, Provenance */; }
  // Layout v2: <case>\<Host>\Network\Zeek_<Log>_<capture>.csv / .log. ZeekJobs.Enqueue/OutputRoot write the same names (host Unknown) when the case has case.json,
  // else the v1 <case>\Network\Zeek\<capture>\ (Tools page, new case folder).

// Core/Triage/SystemSelection.cs: host picker logic of the Triage page (testable without the UI)
public sealed record SystemChoice(DiscoveredSystem System, bool Included, string HostName) { DiscoveredSystem Effective /* System with the trimmed label as HostName */; }
public static class SystemSelection { static string? ValidateHostLabel(label, others) /* null = ok; folder-safe, not reserved (TriagePlanner.HostFolder unchanged), unique case-insensitively — also against the other labels' HostFolder (Timeline → Host_Timeline) */;
  static IReadOnlyList<string?> Validate(choices); Active(choices) /* checked, relabelled */; Labelled(choices) /* all, relabelled */; IsRootIncluded(choices, root);
  static List<DetectedArtifact> Filter(found, choices) /* checked systems only; hits get a System copy with the new HostName */; static List<PcapFile> FilterPcaps(pcaps, choices); }
// Triage page: "Detected systems" card (AutomationId Triage.Systems; rows Triage.Systems.Item.Check / .HostName / .Root / .Acquisition / .HostWarning; Triage.Systems.Summary, .Error).
//   Per system: checkbox (default checked), editable host label (applied on focus loss; invalid or duplicate label = red border + message, Run disabled), trimmed root with tooltip,
//   acquisition in words (Live volume / Mounted image or raw copy / Copied collection; info tooltip: copied → file-system Created/Accessed times reflect the copy), OS,
//   host-name source (warning icon for "folder name" / "default": host name not read from the SYSTEM hive). Ticking or relabelling re-plans artifacts, persistence, captures
//   (ticks of kept jobs survive) and the computer-details step; counts in the summary line reflect checked systems only.
// Core/Settings (append-only): string? ZeekPath.  Timeline mappings: zeek-* source types (pathPatterns (^|\\)Zeek\\, requiredHeaders DateTimeUtc;
//   layout v2: outputNames ^Zeek_<Log>(_...)*\.csv$, artifactIds zeek-<log>).
```

```csharp
// Core/Execution additions (appended): JobSpec(..., Func<JobPostProcessContext, CancellationToken, Task>? PreProcess = null /* after the output dir
//   exists, before the process starts; exception = Failed ("Preparation failed: ..."), process not started; OCE = Cancelled */,
//   Action<Job>? Finally = null /* always once at the end (also cancel / timeout / pre-process failure); exceptions logged */)

// Core/Persistence (built-in offline persistence collector; portable, no Windows APIs; reuses Core/SystemInfo IHiveOpener / ZimmermanHiveOpener / InMemoryHive)
public static class PersistenceCategory { RunKeys, Winlogon, AppInit, ImageHijacks, AppCert, Lsa, Print, Services, Drivers, BootExecute, ActiveSetup, Explorer,
  ComHijack, TerminalServer, Netsh, TimeProviders, CommandProcessor, LogonScripts, Screensaver, Debuggers, ScheduledTasks, StartupFolder, Wmi /* display strings */ }
public sealed class PersistenceEntry { Category, Location, EntryName, Command, ImagePath, User, Enabled ("Yes"/"No"/""), List<string> Flags, Source, Notes;
  DateTime? KeyLastWriteUtc, TaskRegisteredUtc, FileCreatedUtc, FileModifiedUtc; (DateTime?, string) Primary /* task > key > file modified > created */; Flag(string) }
public sealed class ScheduledTaskInfo { TaskPath, XmlFile, Author, RegistrationDateRaw, RegisteredUtc, RegisteredSource, LastRunUtc, Triggers, Actions (TaskAction),
  UserId, LogonType, RunLevel, Enabled, Hidden, TaskCacheId, InTaskCache, XmlMissing, FileModifiedUtc, Error }
public sealed class WmiFinding { ConsumerType, ConsumerName, FilterName, Command, ScriptingEngine, FilterQuery, Namespace, LikelyLegitimate, ConsumerOffset, FilterOffset }
public sealed class PersistenceResult { SystemRoot; Entries; Tasks; Wmi; Hives (HiveInfo); Notes; CountsByCategory() }
public static class RegistryAseps { FromSoftware(hive, result, OfflineChecker?); FromSystem(...); FromUser(ntuser, user, ..., software?); FromUserClasses(...);
  CurrentControlSet(system); ClsidServer(software, clsid, wow); StartupFolderState(hive, hkcu, file); FileTime(long) }
public static class ScheduledTasks { TaskCacheKey; ParseFile(file, tasksRoot); Parse(xml, info); ReadFolder(root, notes, ct); MergeTaskCache(tasks, software?, activeTimeBias?, notes);
  ParseActions(byte[]) /* TaskCache Actions blob: Exec 0x6666, COM 0x7777 */; ToEntries(task, tasksFolderCollected = true) }
public static class StartupFolders { CommonRelative, UserRelative; Read(dir, user, result, checker, approvedHive, hkcu) }   public static class LnkTarget { Read(path); Parse(bytes) -> LnkInfo }
public static class WmiPersistence { RepositoryPath(root); Scan(path|stream, ct) /* 16 MB chunks + 256 KB overlap, PyWMIPersistenceFinder approach */; ToEntry(finding, file); KnownLegitimate }
public static class PersistenceHeuristics { flag names; Apply(entry, OfflineChecker?); ImageFromCommand(cmd); IsWindowsHostedDll(bin, cmd) }
public static class OfflinePath { Resolve(windowsPath, systemRoot, user?) /* env vars, \SystemRoot\, \??\, C:\ -> root; null when not checkable */; CanCheck(resolved) }
public sealed class OfflineChecker { OfflineChecker(root); bool IsMissing(imagePath, user) /* cached folder test */ }
public sealed class PersistenceCollector { const ToolId = "EzForensics.Persistence" (LegacyToolId "EzForensics.Persistence" in older manifests, normalised by RunManifest.Load), Category = "Persistence", Switch = "--persistence", MainCsv = "EzForensics_Persistence.csv",
  TasksCsv = "EzForensics_ScheduledTasks.csv", WmiCsv = "EzForensics_WmiPersistence.csv"; static string[] Headers; PersistenceCollector(IHiveOpener? = null);
  static HasWindowsSystem(root); static UserProfiles(root) -> Profile(Name, Dir, NtUser?, UsrClass?);
  PersistenceResult Collect(root, log?, ct); static Write(result, outDir) -> files; static int RunCommandLine(args, stdout, ct, opener?) /* --root ... | --source; --out */;
  static FolderName(root); static WriteCsv / Esc }
// Replaces Sysinternals Autoruns (removed). Extra columns: Description, Company, Version (PE version resource); Signer, MD5, SHA-1, PESHA-1, PESHA-256, SHA-256, IMP present but empty (reserved for the hash and signature check).
// Core/Triage
public static class PersistenceTriage { const RuleId = "persistence", PersistenceName = "Persistence";
  SystemRoots(layout); BuiltInSpec(exe, root, outDir, label?);
  Plan(IReadOnlyList<DiscoveredSystem> systems | IReadOnlyList<string> roots /* host = root folder name */, caseDir, appExe)
  /* built-in job per system (ticked) 
     layout v2: <case>\<Host>\Persistence\Persistence.csv, Persistence_ScheduledTasks.csv, Persistence_Wmi.csv */ }

// Core/Sysinternals (Windows-only pieces behind OS checks; Core still builds and the collector runs on Linux)
public sealed record SignatureCheck(bool Valid, bool IsMicrosoft, string? Signer, string? Thumbprint, string Detail) { bool Trusted; }
public interface ISignatureVerifier { SignatureCheck Verify(string file); }  SignatureVerifiers.Default /* AuthenticodeVerifier (WinVerifyTrust) on Windows, stub elsewhere */
public static class ProcessElevation { IsElevated() }
// Timeline columns: persistence (EzForensics_Persistence.csv or the older EzForensics_Persistence.csv, v2 Persistence.csv: KeyLastWriteUtc M..., TaskRegisteredUtc ...B, FileCreatedUtc ...B, FileModifiedUtc M...),
//   scheduled-task / WMI details (ignore), autoruns (Autoruns_*.csv from older cases, still displayed; DateTimeUtc M...).
// App: EzForensics.exe --persistence ... runs the collector headless (App.xaml.cs). 
```

## Output file names (Core/Execution/OutputNaming.cs) — layout v1 / jobs without an OutputPlan

Applies to jobs whose `JobSpec.Output` is null (Tools page, `ZeekJobs.Enqueue`, cases from before layout v2). Triage jobs use
**Case layout v2** below (their names come from the rename table; the strip-prefix setting does not apply to them).
```csharp
public sealed record OutputRename(string OriginalPath, string FinalPath) { string OriginalName, FinalName; }
public static class OutputNaming { static IReadOnlyList<string> FileNameFlags /* --csvf --jsonf --xmlf --htmlf --bodyf */;
  static bool TryStripTimestampPrefix(string name, out string stripped); static string StripTimestampPrefix(string name); static bool HasTimestampPrefix(string name);
  static bool IsRunStamp(string digits); static bool IsTimestampFolderName(string name); static string CanonicalName(string fileName);
  static string WithCopyNumber(string path, int n); static string UniquePath(string path); static ISet<string> UserNamedFiles(IReadOnlyList<string> args); }
public sealed class OutputSnapshot { static OutputSnapshot Capture(string dir); string Root; bool IsNewOrChanged(string file); bool IsNewDirectory(string dir); }
public static class OutputRenamer { static IReadOnlyList<OutputRename> StripTimestampPrefixes(OutputSnapshot before, IEnumerable<string>? userNamed = null,
  string? toolId = null, Action<string>? log = null); static string MoveUnique(string source, string target); }
// JobQueue.StripTimestampPrefix (default true, read at job start); Job.OutputRenames; ManifestOutputFile.OriginalName / OriginalPath (omitted when not renamed).
// Core/Settings (append-only): AppSettings.StripTimestampPrefix (bool, default true). AppServices keeps JobQueue.StripTimestampPrefix in sync;
//   SuperTimelineViewModel sets TimelineOptions.TimestampOutputName = !StripTimestampPrefix. Settings page checkbox (AutomationId Settings.StripTimestampPrefix), labelled "Tools page: remove timestamp prefix from output file names": it only affects Tools-page jobs (triage names always follow the convention).
// Core/Execution (append-only): enum ToolLogLevel { Normal, Debug }; AppSettings.ToolLogLevel (default Normal); JobQueue.LogLevel (read at job
//   start, kept in sync by AppServices for the case and maintenance queues); Job.DebugLogging; JobPostProcessContext.Debug. In Debug the queue
//   writes ToolDebugLog.StartLines (quoted command line, working dir, elevation, tool path/version/size/signature) before the process and
//   ToolDebugLog.EndLines (exit code, duration, last 20 stdout/stderr lines) after it, every line prefixed "# Debug: ".
//   Options > Installation row "Tool logging" (AutomationId
//   Settings.ToolLogLevel, HelpTip Settings.Help.ToolLogging).
// Core/Advisories + Core/Settings (append-only): AppSettings.ShowAnalyzeHints (bool, default true; Options > Display row "Analyze hints" Show/Hide,
//   AutomationId Settings.ShowAnalyzeHints, HelpTip Settings.Help.AnalyzeHints); static AnalyzeHints { IsVisible(showSetting, hideThisSession);
//   MenuChecked; MenuEnabled }. Hints = the advisory banner (EventsPane.AdvisoryBannerHost) and the grid-header advisory markers; the advisory
//   data is always computed. Session switch: AppServices.HideHintsThisSession (memory only) via Analyze > Actions > "Hide hints (this session)"
//   (Results.Actions.HideHints; checked + disabled with the tooltip "Hidden in Settings > Options > Display" when the setting is Hide).
//   ResultsViewModel.HintsVisible / HideHintsChecked / HideHintsEnabled update live from the settings PropertyChanged.
```
- **Prefix:** EZ tools name outputs `<run time>_<name>`: `yyyyMMddHHmmss_` (14 digits, most tools; local or UTC time depending on the tool) or
  `yyyyMMddHHmmssffffff_` (20 digits, SQLECmd). A prefix is `^\d{14,21}_` whose first 14 digits parse as a date (1990–2199); anything else
  (`20261007_x.csv`, `12345_x.csv`, a name that would become a bare extension) is left alone. RECmd also writes its plugin files into a
  `<run time>` folder as `<run time>_<Plugin>__<hive>.csv`. SBECmd (`<user>_UsrClass.csv`) has no prefix.
- **When:** after every EZ tool job run by `JobQueue` (Triage, Tools page, anything else) while `StripTimestampPrefix` is on, before the outcome is
  classified (job reasons count the final files). Jobs with an explicit `JobSpec.ExePath` (plaso, Python, Docker) are never renamed. The output folder
  is snapshotted before the process starts; only files that are new or whose size / last-write time changed are renamed, and only inside
  `JobSpec.OutputDir`. Files named with `--csvf`/`--jsonf`/`--xmlf`/`--htmlf`/`--bodyf` are never touched. No `--csvf`-style switches are added to
  command lines: they cannot name multi-file outputs (RECmd plugins, SQLECmd, SrumECmd, AmcacheParser) and would change the recorded argv.
- **Rules:** strip the prefix (`20261007155643_TypedURLs__NTUSER.DAT.csv` -> `TypedURLs_NTUSER.DAT.csv`); new run-time-only folders are flattened into
  their parent and removed when empty; new prefixed folders lose the prefix; for RECmd the first `__` (plugin / hive separator) becomes `_`.
- **Collisions:** never overwrite. When the clean name is taken (an earlier run into the same folder, or two files of one run with the same name, e.g.
  SQLECmd maps for two browser profiles) the file becomes `Name (2).ext`, `Name (3).ext`, ... (number before the last extension:
  `TypedURLs_NTUSER.DAT (2).csv`). Files of one run are processed in path order, so the earliest stamp gets the plain name. Users / hives stay
  separated by the planner's `perMatch` sub-folders (`Registry\RECmd\Users_bob_NTUSER.DAT\...`).
- **Custody:** the run manifest hashes the final files (a same-volume rename never changes content) and records `originalName` and `originalPath` for
  each renamed file. The job log gets `# Renamed N output file(s) ...`.
- **Consumers:** code that recognises EZ outputs by name must use `OutputNaming.CanonicalName` (no prefix, no ` (n)` suffix) so old prefixed and new
  names both work. Timeline `mappings.json` file-name / artifact / user regexes are applied to the canonical name; write them as `(^|_)Tool_Output\.csv$`.
- **Super timeline (EZ CSV merge):** default output `<case>\Timeline\SuperTimeline_EZ.csv`, else the first free `SuperTimeline_EZ (n).csv`; with
  `TimelineOptions.TimestampOutputName` the old `SuperTimeline_<yyyyMMddTHHmmssZ>.csv`. plaso naming is unchanged (`Timeline\plaso\<stamp>.plaso`,
  `Timeline\SuperTimeline_plaso_<stamp>.csv`).

## Case layout v2 (multi-host, artifact naming, provenance)

New triage runs write `<case>\<Host>\<Category>\<Artifact>[_<Variant>][_<Qualifier>].csv`. No tool folders; the host level is always present (also
for one system). Per-user / per-hive / per-capture variants are qualifiers in the file name, never sub-folders. Old cases (v1:
`<case>\<Category>\<ToolId>[\<per-match>]\<tool name>.csv`) still open: the Results tree lists folders as they are, the timeline scanner reads both,
`CaseIndex` infers provenance for both. Case-level folders stay: `Timeline`, `logs`, `SystemInfo`, the hidden `.ezstaging` and the hidden `.ezcache` (Analyze combined views).

**Name rules (strict).** `<Artifact>` = the rule's `output.name` (PascalCase, no tool prefix). `<Variant>` = from the rename table (alphanumeric
segments joined by `_`; Zeek logs in PascalCase). `<Qualifier>` = alphanumeric parts joined by `_` (`ArtifactNaming.SanitizeQualifier`:
`john.doe` → `johndoe`, no ASCII letter/digit → `X<8 hex>`). Every CSV name passes `ArtifactOutputLayout.IsValidOutputName`
(`^[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)*\.csv$`). Collisions (earlier run, two files of one run mapping to one name) → `Name_2.csv`, `Name_3.csv`
(never overwrite, never ` (2)`). Non-CSV outputs keep their extension (`Zeek_Conn_cap.log`). The planner also numbers
repeated qualifiers of one host + rule (`ActivitiesCache_bob`, `ActivitiesCache_bob_2`). Host folder = `TriagePlanner.HostFolder(DiscoveredSystem.HostName)`
(`ArtifactOutputLayout.SafeHostFolder`; a host named like a case-level folder or a category gets `Host_`).

**Mechanics.** Each planned job writes into its own staging folder `<case>\.ezstaging\<host>_<rule>[_<qualifier>]_<hash8>` (its `{out}`; the
`.ezstaging` folder is Hidden). After the process (and `PostProcess`) `OutputPublisher.Publish` moves every new file into `JobSpec.OutputDir`
(= `<case>\<Host>\<Category>`) under its final name, flattening sub-folders, then removes the empty staging folders. This is what lets parallel
jobs share one category folder. Single-CSV tools get `--csvf <Artifact>[_<Qualifier>].csv` appended by the planner (`output.csvf: true`, only for
tools whose catalog defines `--csvf`); the rename table still recognises their default names. `JobPostProcessContext.OutputDir` is the staging folder.
`JobQueue.Enqueue` writes `<case>\case.json` (`{ "kind": "EzForensics.Case", "layoutVersion": 2, ... }`, never rewritten) for jobs with an `OutputPlan`.

| Rule (tool) | Category | Output name(s) | Artifact id(s) |
|---|---|---|---|
| prefetch (PECmd, `--csvf`) | ProgramExecution | `Prefetch.csv`, `Prefetch_Timeline.csv` | prefetch, prefetch-timeline |
| evtx (EvtxECmd, `--csvf`) | EventLogs | `EventLogs.csv` | eventlog |
| mft (MFTECmd, `--csvf`) | FileSystem | `MFT.csv` (+ `MFT_Boot`, `MFT_SDS`, `MFT_I30`, `MFT_LogFile` for those inputs) | mft, mft-boot, mft-sds, i30, mft-logfile |
| usnjrnl (MFTECmd, `--csvf`) | FileSystem | `UsnJournal.csv` | usnjournal |
| amcache (AmcacheParser) | ProgramExecution | `Amcache_ProgramEntries`, `Amcache_AssociatedFileEntries`, `Amcache_UnassociatedFileEntries`, `Amcache_DeviceContainers`, `Amcache_DevicePnps`, `Amcache_DriveBinaries`, `Amcache_DriverPackages`, `Amcache_Shortcuts` (.csv) | amcache-program-entries, amcache-associated-file-entries, ... (`amcache-<kebab table>`) |
| appcompat (AppCompatCacheParser, `--csvf`) | ProgramExecution | `ShimCache.csv` | shimcache |
| registry (RECmd batch, per hive) | Registry | `Registry_SYSTEM.csv`, `Registry_SOFTWARE.csv`, `Registry_SAM.csv`, `Registry_SECURITY.csv`, `Registry_NTUSER_<user>.csv`, `Registry_UsrClass_<user>.csv`; plugin files `Registry_<Plugin>_<hive qualifier>.csv` | registry, registry-plugin |
| shellbags (SBECmd, per user) | FileFolderAccess | `ShellBags_UsrClass_<user>.csv`, `ShellBags_NTUSER_<user>.csv` (`ShellBags_Deduplicated_<user>`, `ShellBags_Messages_<user>.txt`) | shellbags |
| lnk (LECmd, `--csvf`, per user) | FileFolderAccess | `LNK_<user>.csv` | lnk |
| jumplists (JLECmd, per user) | FileFolderAccess | `JumpLists_Auto_<user>.csv`, `JumpLists_Custom_<user>.csv` | jumplist-automatic, jumplist-custom |
| recyclebin (RBCmd, `--csvf`) | FileDeletion | `RecycleBin.csv` | recyclebin |
| srum (SrumECmd) | ProgramExecution | `SRUM_<Table>.csv` (`SRUM_NetworkUsages`, `SRUM_AppResourceUseInfo`, ...) | srum |
| sum (SumECmd) | UserActivity | `UAL_<Table>.csv` (`UAL_SUMMARY_ClientsDetailed`, `UAL_DETAIL_DnsInfo`, ...) | ual |
| activitiescache (WxTCmd, per user) | UserActivity | `ActivitiesCache_<user>.csv`, `ActivitiesCache_PackageIDs_<user>.csv` (other tables `ActivitiesCache_<Table>_<user>`) | activitiescache, activitiescache-packages |
| recentfilecache (RecentFileCacheParser, `--csvf`) | ProgramExecution | `RecentFileCache.csv` | recentfilecache |
| sqlite-hunt (SQLECmd) | Databases | `SQLite_<map name segments>.csv` | sqlite |
| persistence (EZ Forensics built-in) | Persistence | `Persistence.csv`, `Persistence_ScheduledTasks.csv`, `Persistence_Wmi.csv` | persistence, persistence-tasks, persistence-wmi |
| pcap (Zeek, per capture) | Network | `Zeek_<Log>_<capture>.csv` and `.log` (`Zeek_Conn_cap`, `Zeek_DceRpc_cap`, `Zeek_KnownHosts_cap`) | zeek-<log kebab> (zeek-conn, zeek-dce-rpc, ...) |
| imported / unknown | `output.name` default = PascalCase tool id | `<Name>_<file stem segments>.csv` | rule id (kebab) |

Older cases may still contain `Autoruns_*.csv` (+ `Autoruns_Raw_*.txt`) from the removed Autoruns job; Analyze still displays them.

Rename table (`ArtifactNaming.RenameTable`, matched on the tool's file stem: run-time prefix, a leading `!` and the extension removed; first match
of the tool's own rules, then the fallback `*`): PECmd `^(PECmd_Output|{csvf})_Timeline$` → `{base}_Timeline`, `^(PECmd_Output|{csvf})$` → `{base}`;
EvtxECmd `^(EvtxECmd_Output|{csvf})$`; MFTECmd `^MFTECmd_\$MFT_Output$` → `MFT`, `\$J` → `UsnJournal`, `\$Boot` → `MFT_Boot`, `\$SDS` → `MFT_SDS`,
`\$I30` → `MFT_I30`, `\$LogFile` → `MFT_LogFile`; AmcacheParser `^Amcache_ShortCuts$` → `{base}_Shortcuts`, `^Amcache_(?<v>.+)$` → `{base}_{v}`;
AppCompatCacheParser `^(AppCompatCache|{csvf})$`; RECmd `^RECmd_Batch_.*_Output$` → `{base}`, `^(?<v>[^_]+)__.*$` → `{base}_{v}` (plugin);
SBECmd `^.+_(?<v>UsrClass|NTUSER)$` → `{base}_{v}`; LECmd `^(LECmd_Output|{csvf})$`; JLECmd `^AutomaticDestinations$` → `{base}_Auto`,
`^CustomDestinations$` → `{base}_Custom`; RBCmd `^(RBCmd_Output|{csvf})$`; SrumECmd `^SrumECmd_(?<v>.+)_Output$`; SumECmd `^SumECmd_(?<v>.+?)(_Output)?$`;
WxTCmd `^(.+_)?Activity$`, `^(.+_)?Activity_PackageIDs$`, `^(.+_)?Activity_?(?<v>.+)$` (the optional leading `<folder>_` WxTCmd adds is dropped: the user is the qualifier); RecentFileCacheParser `^(RecentFileCacheParser_Output|{csvf})$`; SQLECmd `^(?<v>.+)$`;
EzForensics.Persistence `^(EzForensics|EzHub)_Persistence$`, `^(EzForensics|EzHub)_ScheduledTasks$` → `_ScheduledTasks`, `^(EzForensics|EzHub)_WmiPersistence$` → `_Wmi`; 
Zeek `^(?<v>.+)$` → `{base}_{V}`; fallback `^{csvf}$` → `{base}`, `^(?<v>.+)$` → `{base}_{v}`.
`ArtifactNaming.IdentifyNewName(name)` / `IdentifyLegacyName(toolId, name)` classify files without a manifest entry.

```csharp
// Core/Execution (appended JobSpec parameters; existing callers unchanged)
//   JobSpec(..., OutputPlan? Output = null, JobProvenance? Provenance = null)
public sealed record OutputPlan(string StagingDir, string ArtifactName, string ArtifactId, string? Qualifier = null, string? FixedFileName = null)
  { const string StagingFolderName = ".ezstaging"; }
public sealed record InputSelector(string Path, string? Mask = null, bool Recurse = false);
public sealed record JobProvenance { string RuleId; string? ArtifactId, Host, HostNameSource, SystemRoot, AcquisitionKind /* LiveVolume|MountedImageOrRawCopy|CopiedCollection */,
  OsFamily; IReadOnlyList<InputSelector> Inputs; bool HashInputs = true, HashOnLiveVolume = false; bool IsLiveVolume; }
public sealed record OutputNameRule(string ToolId, string Pattern, string Template, string ArtifactId);
public sealed record PublishedOutput(string OriginalPath, string FinalPath, string ArtifactId);
public static class ArtifactNaming { RenameTable; NewNamePatterns; SanitizeQualifier(string?); Qualifier(params string?[]); Variant; Pascal; KebabId; ToolStem;
  (string Stem, string ArtifactId) Map(toolId, OutputPlan, fileName); string FinalFileName(toolId, plan, fileName); string WithNumber(path, n) /* Name_n.ext */;
  (string ToolId, string ArtifactId)? IdentifyNewName(fileName); string? IdentifyLegacyName(toolId, fileName); }
public static class OutputPublisher { Publish(toolId, plan, finalDir, OutputSnapshot stagingBefore, log?, partial = false); MoveNumbered(src, target);
  RemoveEmptyStaging(dir); PrepareStaging(dir); const PartialQualifier = "PARTIAL"; PartialName(name) /* MFT.csv → MFT_PARTIAL.csv */;
  IReadOnlyList<string> Leftovers(stagingBefore) /* this job's new/changed files still in staging */ }
// Staging rules: JobQueue claims a job's StagingDir while it runs; a second job with the same StagingDir (second run of one plan) fails with
//   "Another job is still writing to the same staging folder…". Stale files already in a staging folder are never published or listed (snapshot
//   diff). Cancelled / timed-out jobs publish with partial = true (`*_PARTIAL.csv`; Job.PartialOutputs, ManifestJob.PartialOutputs = true,
//   Details output.partial). Files that could not be moved stay in staging: Job.StagingLeftovers, ManifestJob.LeftInStaging, Details
//   output.leftInStaging, and the status text ends with "N output file(s) left in staging: <dir>".
// Triage page: Run selected is disabled while the case folder has queued / running layout v2 jobs; a re-plan happens when any selected job's
//   OutputDir is not under the case folder (separator-aware: C:\case2 is not under C:\case).
public static class InputFacts { MaxFiles = 5000; MaxHashFileBytes = 64 MB; MaxHashBytesPerJob = 512 MB; LiveVolumeNote, LiveVolumeHashedNote;
  (List<ManifestInputFact>, string? Note) Collect(inputs, hashInputs, isLiveVolume, hashOnLiveVolume = false, ct) /* FileInfo times (reading metadata
    never changes them); hashes = hashInputs && (!live || hashOnLiveVolume); a live volume ALWAYS gets a note: LiveVolumeNote (hashes skipped) or
    LiveVolumeHashedNote (hashed on request; reading may update access times) */ }
// Job: PublishedOutputs, InputFacts, InputFactsNote (captured before PreProcess and the process). Job.OutputRenames = published moves.

// Run manifest (all new members omitted when null; old manifests load unchanged: RunManifest.Load / TryLoad, ManifestWriter.ReadOptions)
//   RunManifest: int? LayoutVersion (2); string? CaseDir; List<ManifestSource>? Sources { RootPath, Host, HostNameSource, AcquisitionKind, OsFamily }
//   ManifestJob: Category, RuleId, ArtifactId, Host, SystemRoot, AcquisitionKind, StagingDir, List<ManifestInputFact>? InputFacts
//     { Path, CreatedUtc, ModifiedUtc, AccessedUtc, Size, Sha256? }, InputFactsNote. Layout v2 jobs list only their published files (+ leftovers in staging).
//   ManifestOutputFile: ArtifactId, ToolId (every job), Host; OriginalName / OriginalPath = the tool's name / staging path.

// Core/Triage
public sealed record TriageScanResult(string Source, SourceLayout Layout, IReadOnlyList<DiscoveredSystem> Systems, List<DetectedArtifact> Artifacts)
  { static ForFile(file, note); }   // Systems: host, root, acquisition, host-name source, OS — for the host picker UI
public static class ArtifactScanner { const FromLiveSystem = "live system";
  static TriageScanResult ScanSource(string source, rules? = null, IHiveOpener? = null, int maxDepth = 3, CancellationToken ct = default)
    /* HostDiscovery; nothing found → SourceLayoutDetector roots; a live system drive without a readable SYSTEM hive is named Environment.MachineName */;
  static List<DetectedArtifact> Scan(IReadOnlyList<DiscoveredSystem>, rules, ct); static List<DetectedArtifact> Scan(SourceLayout, ...) /* no System */;
  static DiscoveredSystem SystemFor(root, opener?, readHive = true, isRunningOsRoot?); static IReadOnlyList<DiscoveredSystem> DiscoverSystems(source, opener, maxDepth, out notes, isRunningOsRoot?); }
// Live volume = ONLY the root of the running OS volume (SourceLayoutDetector.IsRunningOsRoot: Path.GetPathRoot(Environment.SystemDirectory), else
//   %SystemDrive%, ready NTFS). It is one system and never walked. Any other drive root (Arsenal-mounted E01, USB disk reporting Fixed, KAPE drive)
//   is walked like a folder (at a volume root $MFT alone does not make a system root) and classified MountedImageOrRawCopy / CopiedCollection.
//   SourceLayoutDetector.IsLiveVolumeRoot(source, isRunningOsRoot?), IsRunningOsRoot(path), IsVolumeRoot(path), LooksLikeSystemRoot(dir, ignoreMft = false),
//   Detect(source, isRunningOsRoot?); HostDiscovery.Discover(source, maxDepth, opener, isRunningOsRoot?), Kind(root, isRunningOsRoot?) — the predicate is for tests.
public sealed class TriageRule { ...; RuleOutput? Output; RuleOutput EffectiveOutput; }   // rules.json "output": { name, artifactId, csvf, qualifier }
public sealed class RuleOutput { string Name, ArtifactId; bool Csvf; OutputQualifier Qualifier /* none|user|hive|file */; static Default(rule); }
public static class TriagePlanner { ...; HostFolder(host); HostOf(artifact, root); Compose(name, q); StagingDir(caseDir, host, ruleId, q, matched);
  QualifierFor(kind, matched, root, perMatch); UniqueQualifier(used, host, ruleId, q); SafeCategory(c); InputsFor(rule, matched, root) }
  // Labels get "<host> · " when the plan spans several hosts.

// Core/Case
public enum CaseLayoutVersion { Unknown = 0, Legacy = 1, HostArtifact = 2 }
public static class CaseLayout { const MarkerFileName = "case.json", MarkerKind = "EzForensics.Case"; const int CurrentVersion = 2;
  static CaseMarker? ReadMarker(caseDir); static void EnsureMarker(caseDir, version = 2) /* never rewrites */; static string MarkerPath(caseDir); }
public sealed class CaseIndex { static CaseIndex Load(caseDir) /* all EzForensics_manifest_*.json and legacy EzHub_manifest_*.json, in run order (ManifestFiles); paths rebased when the case moved (manifest caseDir, else
  the parent of the jobs' logs folder) */; string CaseDir; CaseLayoutVersion LayoutVersion /* marker > manifests > folders */; bool HasLegacyOutputs /* mixed when v2 */;
  Manifests; Sources; Hosts; CsvProvenance Describe(string csvPath); string? HostOf(path); static IsKnownCategory(name); static CaseLevelFolders;
  static FromPath(relativePath) -> (Layout, Host, Category, ToolFolder); static LegacyToolId(category, folder) /* Persistence\EzHub or Persistence\EzForensics → EzForensics.Persistence */;
  static AcquisitionKind? ParseAcquisition(string?); static EvidenceAcquisition ToEvidenceAcquisition(AcquisitionKind?);
  static string? AdvisoryArtifactId(string? artifactId) /* amcache-* → amcache, jumplist-* → jumplist; null when the advisories lack it */; }
// CaseIndex.Load replays manifest jobs (at EndUtc) and logs\file_operations.log lines (at their time) in time order: RENAME / MOVE move a file's
//   (or a folder's files') provenance, COPY / DUPLICATE copy it, DELETE drops it — so renamed files keep provenance and a reused name does not
//   inherit an old one. Describe ignores a manifest entry whose file size differs from the recorded size, or (file written > 2 s after the
//   job ended) whose SHA-256 differs; it then falls back to path / name inference.
public sealed record CsvProvenance { Path, RelativePath, ToolId, ArtifactId, Host, Category, RuleId, SystemRoot, AcquisitionKind? Acquisition,
  IReadOnlyList<ManifestInputFact> InputFacts, InputFactsNote, ManifestJob? Job, ManifestPath, CaseLayoutVersion Layout, ProvenanceSource Source
  /* Manifest | NewLayoutPath | LegacyPath | FileName | Unknown */; EvidenceAcquisition EvidenceAcquisition; IReadOnlyList<InputTimestampFact>? InputTimestampFacts;
  string? AdvisoryArtifactId; }
// Timeline: TimelineScanner.Scan builds a CaseIndex; only a manifest artifact id feeds TimelineMappings.Match; inputs under .ezstaging, .ezcache and
//   any Merged folder, and every logs\derived_outputs.json output are skipped whether or not Hidden (IsExcluded(rel, derivedOutputs?), DerivedOutputs(caseDir));
//   TimelineInput.Host = manifest host / v2 host folder; generic CSVs use the known ToolId as Source.
```

## Conventions
- Argument templates in JSON use the tokens `{file}`, `{dir}`, `{out}`, `{root}` and `{toolDir}`. Every token expands to exactly one argv element.
- Start every process with `ProcessStartInfo.ArgumentList`, never a quoted string. The working directory is the folder that holds the exe.
- Never bundle EZ binaries. Never invoke kape.exe.

## Product name, data folder, installer, setup wizard (EZ Forensics)

```csharp
// Core/Settings/AppPaths.cs. Product "EZ Forensics"; exe EzForensics.exe (AssemblyName); namespaces / projects are EzForensics.*.
public static class AppPaths { const ProductName = "EZ Forensics", PackId = "EzForensics", DataFolderName = "EzForensicsData", LegacyFolderName = "EzHub";
  static string? Env(name) /* EZFORENSICS_<name>, else EZHUB_<name> */; DataRoot /* %LOCALAPPDATA%\EzForensicsData | EZFORENSICS_DATA */;
  LegacyRoot /* %LOCALAPPDATA%\EzHub, read only */; InstallRoot /* %LOCALAPPDATA%\EzForensics = Velopack app dir, wiped on uninstall: never store data */;
  SettingsPath (EZFORENSICS_SETTINGS), LogsDir, DefaultToolsRoot, CacheDir, FlagsDir, PrivateDotnetDir (EZFORENSICS_DOTNET_DIR);
  static string ComponentRoot(component, envName, legacyInUse?) /* env > legacy EzHub\<component> while in use (venvs are not relocatable;
    only for the default data root) > DataRoot\<component> */; }
// PlasoLocator.ManagedRoot = ComponentRoot("plaso", "PLASO_ROOT", venv|python exists); ZeekLocator.ManagedRoot = ComponentRoot("zeek", "ZEEK_ROOT", state.json).
public static class DataMigration { const MarkerName = "migrated-from-ezhub.json"; static MigrationResult Run(legacyRoot?, dataRoot?, settingsPath?)
  /* copies settings.json + flags/cache *.json once; never moves / deletes; skipped when EZFORENSICS_SETTINGS / _DATA is set */ }
// Core/Tools/RuntimeProbe: HasSystemNet9(), HasPrivateNet9(), HasFramework(root, fw, major), PrivateDotnetRoot, Reset(),
//   DotnetRootEnvironment() /* DOTNET_ROOT(_X64) = private dir, only when no system .NET 9 */, ApplyToCurrentProcess() (App startup + after install:
//   every child process inherits it; verified: net9 apphosts (MFTECmd, TimelineExplorer) resolve NETCore + WindowsDesktop 9.0.20 from DOTNET_ROOT_X64).
//   The private runtime = dotnet-install.ps1 -Runtime dotnet AND -Runtime windowsdesktop (windowsdesktop alone has no Microsoft.NETCore.App).

// Core/Setup (UI-independent; a future "ezforensics setup --profile recommended" CLI uses the same API)
public sealed class SetupCatalog { static Default; Bundles (artifacts /* incl. the persistence collector */, timeline,
  network, memory); Find(id); IsNet9Only(zip); EzZipMB; }
// Windows artifacts: every selectable item DefaultOn (EZ CLI, sync, private .NET 9), so Recommended
// ticks the bundle fully. ForProfile no longer drops RequiresNet9 items without .NET 9 (the private dotnet9 item is selected with them) and never
// selects dotnet9 when the machine has .NET 9 (the wizard shows it ticked + disabled, "Already installed").
public static class ThirdPartyLicenses { All (ThirdPartyLicense(Component, License, Url): EZ tools, plaso, Volatility 3, Zeek,
  RITA/ClickHouse, Ubuntu Base, .NET 9, Python); Text }
public sealed class SetupItem { Id, Title, Description, DefaultOn, BuiltIn, ComingSoon, OptIn, Hidden /* not in the wizard, never selected by a profile; comes in via Requires */, DownloadMB, DiskMB, RequiresNet9, Requires, License, Zips }
public static class SetupIds { BundleArtifacts..., EzCli /* Kind=cli zips (the GUI viewers and the Utilities tools are not in the catalog / setup; SetupCatalog.NotInSetup removed; ids ez-timeline-explorer, ez-gui-viewers retired) */, EzSync, Dotnet9, PersistenceBuiltIn, ComputerDetails, Plaso,
  EzDistro /* "ez-wsl-distro", Network bundle, Hidden; Zeek Requires it (not when Zeek already works), RITA always */, Zeek, Rita, Volatility }
public enum SetupProfile { Recommended, Everything, Custom /* the user's own ticks: no radio in the wizard */ }
public sealed class SetupSelection { Items; AcceptedLicenses /* never filled automatically */; static ForProfile(profile, catalog, system?);
  static TryParseProfile(text, out profile); }
public sealed record SystemCheckResult { OsName, OsBuild, FreeDiskBytes, IsAdmin, Wsl (WslStatus), WslDistros /* listed, never started */,
  WslZeekDistro /* no longer probed: null */, EzDistroState (EzDistroInfo?), EzDistroReady, Docker, HasSystemNet9,
  HasPrivateNet9, Python, InternetReachable, InstalledZips, PlasoVersion, ZeekSummary }
public static class SystemCheck { static Task<SystemCheckResult> RunAsync(http?, settings, locator?, probe?, probeEngines = true, ct) }
public static class SetupPlanner { static SetupPlan Plan(catalog, selection, system) /* pure: requirements, .NET 9 auto-add for net9-only items,
  licence gate, net4/net9 choice, already-installed detection, sizes and time (4 MB/s).
  Network on WSL: [enable-wsl (admin, restart)] -> ez-distro (InstallEzDistro; AlreadyInstalled when EzDistroReady) -> zeek (InstallZeekWsl,
  Args distro=EzForensics, DependsOn ez-distro unless already installed). Network is WSL only in setup (RITA is WSL-only; no Docker runtime, no docker pull step);
  the hidden ez-distro step is added whenever Zeek or RITA needs it. WSL not available: one ZeekGuidance step ("Network: WSL is not available").
  The Tools page Zeek card still detects / uses / installs Zeek with Docker (Core DockerRuntime, ZeekSetup.DockerPullStep unchanged).
  Steps are returned through Order(): built-in, then RequiresAdmin steps (enable-wsl: the one UAC prompt right after Install), then everything
  that does not need WSL (dotnet, EZ tools, sync, python, plaso, Volatility, symbols), then WSL/network (ez-distro, zeek, rita) last. */
  static IReadOnlyList<SetupStep> Order(steps) /* stable; dependencies always point to earlier steps */ }
public sealed class SetupPlan { Selection; Steps; Notes; EzNetVersion; TotalDownloadMB; TotalDiskMB; EstimatedDuration; NeedsAdmin; NeedsRestart; ToText(); }
public enum SetupStepKind { BuiltIn, InstallDotnet, DownloadEzTools, SyncMaps, ProvisionPython, CreatePlasoVenv, InstallPlaso,
  InstallVolatility, InstallVolatilitySymbols, EnableWsl, InstallEzDistro, InstallZeekWsl, InstallRita, ZeekGuidance }
public enum SetupStepState { Queued, Running, Done, AlreadyInstalled, Failed, Skipped, Cancelled, WaitingForRestart, ActionNeeded }
public sealed class SetupStep : INotifyPropertyChanged { Id, Kind, Title, Detail, DependsOn, DownloadMB, DiskMB, EstimatedSeconds, RequiresAdmin,
  RequiresRestart, Args, Zips; State, Message, Progress, LogText, SizeText; event LogLine }
public sealed class SetupRunner { SetupRunner(ISetupExecutor); Task RunAsync(plan, IProgress<SetupRunProgress>?, ct) /* in order; dependents of a failed
  step are Skipped, of a restart step WaitingForRestart; re-run = Retry (succeeded steps kept) */; static string Explain(Exception);
  static double Weight(step) /* overall-bar share = max(5, EstimatedSeconds); built-in 1 */; void Tick() /* host calls ~1/s: time-based creep */ }
public sealed class StepProgressEstimate { const CreepCap = 0.9, LineShare = 0.03; Report(fraction?, elapsed); LogLine(); Elapsed(t); Value
  /* reported fraction, plus a creep toward 90% of the remaining share from log lines and time (63% of the gap after EstimatedSeconds);
     never goes back */ }
// Step bar (SetupStep.Progress): null (indeterminate, a sliding segment) until the first log line / tick; a step that never calls
// SetupStepContext.Report gets the estimate published into it (capped at CreepCap), a step that does keeps its real fraction, capped at
// SetupStepContext.MaxRunningProgress (0.99). Only the runner sets 1, after the state left Running (Done / AlreadyInstalled).
// SetupPlan.FormatElapsed(t): "04:07", "1:02:03" (wizard Install page timer, Done page "Total time", resume banner).
public interface ISetupExecutor { Task<SetupStepResult> ExecuteAsync(SetupStepContext, CancellationToken); }
public sealed class DefaultSetupExecutor : ISetupExecutor  // reuses ToolUpdater, JobQueue jobs, PrivatePython, PlasoSetup, ZeekSetup
public sealed class SetupEnvironment { Queue, CreateHttp, Settings, SaveSettings, Locator, MaintenanceDir, Elevation (IElevatedLauncher), Probe,
  AfterStep, SimulatorExe + Simulate /* UI-test hook: EZFORENSICS_SETUP_SIMULATE=<fake exe>, EZFORENSICS_SETUP_SIMULATE_STEPS=<kinds> */,
  IsElevated /* admin steps in-process, no UAC */, ElevatedHelperExe + ElevatedHelperPrefixArgs /* own exe relaunched with --elevated-setup */,
  RebootPending (Func<bool>, default CBS RebootPending key) }
// EnableWsl step: wsl --status works -> AlreadyInstalled (no prompt). Else ONE elevated helper run (UAC; declined -> Failed with
// DefaultSetupExecutor.DeclinedMessage, so ez-distro/zeek/rita are Skipped and the rest installs). Then: works now and no reboot pending -> Done
// (distro/Zeek/RITA continue in the same run); otherwise WaitingForRestart (restart asked once, on the Done page). Never restarts Windows itself.
// Heartbeat: DefaultSetupExecutor.HeartbeatInterval (static, 20 s) + StepHeartbeat(step, text, interval): a quiet step logs
// "<text> (elapsed m:ss)." (texts WslHeartbeat, Wsl1Heartbeat, VenvHeartbeat, PlasoPipHeartbeat, VolatilityPipHeartbeat, ZeekAptHeartbeat,
// DistroHeartbeat) for the WSL step and every step whose progress comes only from log lines.
// "Welcome to WSL" (wslsettings OOBE): SetupEnvironment.WslWelcome (WslWelcomeGuard?, null = off; the app passes CreateDefault()). Before every
// WSL step (DefaultSetupExecutor.IsWslStep: EnableWsl, InstallEzDistro, InstallZeekWsl, InstallRita) MarkOobeComplete() sets
// HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss DWORD OOBEComplete = 1 (RegistryWslOobeStore; read by wslservice.exe) once; after
// the step CloseWelcomeWindows() sends CloseMainWindow (never Kill) to wslsettings processes started at/after WslWelcomeGuard.Since. Both log
// to the step log. EzDistro.RunAsync calls WslWelcomeGuard.MarkOobeCompleteOnce() (once per process) before wsl.exe.
// Maintenance processes never appear on the Jobs page: SetupEnvironment.Queue = AppServices.Maintenance.Queue (App/Services/SetupHost).
public sealed class MaintenanceRunner /* Core/Execution */ { MaintenanceRunner(JobQueue privateQueue, string logRoot); JobQueue Queue; string LogRoot;
  string NewLogDir(name) /* <LogRoot>\<yyyyMMdd_HHmmss>_<name> */;
  Task<Job> RunAsync(JobSpec spec, Action<string>? log, ct = default, string? logDir = null) /* enqueue on the private queue (logs + manifest
  in logDir ?? LogRoot, job log <logDir>\logs\<id>_<tool>.log), every output line -> log, waits; ct cancels the job and then throws OCE */;
  static Task<Job> RunAsync(JobQueue queue, JobSpec spec, string logDir, Action<string>? log, ct = default) }
// App: AppServices.Maintenance = MaintenanceRunner(new JobQueue(Locator, 2, JobTimeoutMinutes) { PreferredFlavor follows the setting,
//   StripTimestampPrefix = false }, AppPaths.LogsDir\maintenance). Constructed on the UI thread. The Tools page uses it (see "Tools tab round"):
//   var job = await S.Maintenance.RunAsync(spec, AppendToCardLog, ct, S.Maintenance.NewLogDir("plaso")).
public static class ElevatedSetupHelper { const Switch = "--elevated-setup", EnableWslTask = "enable-wsl", RebootRequiredExit = 3010;
  BuildArgs(tasks, resultPath, logPath?); int RunCommandLine(args, log?, runner?) /* App.OnStartup: headless, writes ElevatedRunResult JSON to --result;
  --log <file>: every log line is also appended to it, flushed per line (LineLogFile). The executor passes <maint>\elevated\<task>.log and tails it
  (LogFileTailer.TailAsync, PollInterval 500 ms, shared read) into the step log while the helper runs */;
  EnableWsl(runner, wslExe, dismExe, log) /* wsl --install --no-distribution; on failure DISM Microsoft-Windows-Subsystem-Linux + VirtualMachinePlatform
  /all /norestart; 0|3010 ok, 3010 = restart */; Interpret(result, wslUsableNow, rebootPending) -> SetupStepResult; ReadResult/WriteResult; IsRebootPending(); RunHidden }
public sealed class ElevatedRunResult { Ok, RestartRequired, Message, List<ElevatedCommandLog { Command, ExitCode }> Commands }
public static class SetupResumeLaunch { const Switch = "--resume-setup"; RunOnceKey = HKCU Software\Microsoft\Windows\CurrentVersion\RunOnce;
  ValueName = "EzForensicsResumeSetup"; BuildCommand(exe, dll?) /* "exe" --resume-setup */; HasSwitch(args); Register(cmd, key?); Read(key?); Unregister(key?) }
public enum StartupMode { MainWindow, WizardFirst, ResumeInBackground }   public enum ResumeAction { Finished, RestartWindows, Run }
public static class SetupWizardPolicy { Startup(settings) -> StartupMode /* SetupResume pending: ResumeInBackground (main window + background
  run, NO wizard; also the RunOnce --resume-setup launch); genuine first start (!SetupWizardSeen && !SetupWizardDontShow): WizardFirst (the wizard
  alone, MainWindow created after it closes; App.ShutdownMode OnExplicitShutdown meanwhile); else MainWindow (closing / skipping the wizard sets
  SetupWizardSeen: it never opens by itself again; nothing installed = "Tools not installed — Open setup" banner on Ingest / Tools) */;
  ToResume(selection); PendingAfterRestart(plan) /* items of WaitingForRestart steps only, null when none */;
  Resume(plan) -> ResumeAction /* IsEmpty: Finished; NeedsAdmin (WSL still off = not restarted): RestartWindows; else Run */; FromResume(state);
  SelectionKey(selection), PlanIsStale(planKey, current) /* the wizard re-plans before Install / Retry when the ticks changed */;
  FailureText(failedSteps) /* one "Title: first sentence" line per step (<= 90 chars) + FailureHint "Fix it and click Retry, or Finish." */ }
// After a run with steps WaitingForRestart: SetupResume = PendingAfterRestart(plan) + RunOnce registered (App/Services/SetupHost.RecordOutcome);
// after a run that leaves nothing pending and nothing failed both are cleared. App/ViewModels/SetupResumeViewModel (ShellViewModel.SetupResume) runs
// the pending plan with the same SetupRunner/DefaultSetupExecutor (private maintenance queue) behind a banner `Shell.SetupResume` (ItemStatus =
// Hidden|Running|Succeeded|Failed|RestartNeeded; Title and Detail share one line; `.Detail`, `.Elapsed`, `.Progress`, `.Retry`, `.OpenSetup`
// (wizard with the pending items preselected, at the Summary), `.Restart`, `.Dismiss`). Success: SetupResume + RunOnce cleared, banner hides after
// 12 s. Failure: kept. RestartWindows: RunOnce re-registered, banner asks for the restart (no admin step is re-run). Double runs: a static flag. The app is single-instance
// (Core/Setup/SingleInstanceGuard: mutex Local\EzForensics.App + event Local\EzForensics.App.Activate; a second launch, e.g. the RunOnce
// --resume-setup, brings the running window forward and exits; the --elevated-setup / --computer-details / --persistence helper modes are exempt).
// AppServices.SetupResumeActive (pending SetupResume or running) hides the FirstRun.Banner.
// Whenever the wizard closes (not when restarting Windows) the tools folder is rescanned and plaso/Volatility/Zeek/RITA re-probed.
// Core/Settings (append-only): bool SetupWizardSeen, SetupWizardDontShow (legacy, no UI); SetupResumeState? SetupResume (after "enable WSL" + restart; Items + SavedUtc, the old ZeekRuntime value is ignored on load);
//   string? UpdateFeedUrl; bool ThirdPartyLicensesAccepted + DateTime? ThirdPartyLicensesAcceptedUtc (the wizard's one licence box, recorded on
//   Install, log source "setup wizard"); int? EzDistroWslVersion (1 = WSL 2 import fell back).
// App/Controls/HelpTip (implicit style in Themes/Controls.xaml): circled "?" whose Text shows as a tooltip on hover, keyboard focus and
// click / Enter / Space (Esc closes); UIA Button "Help" with HelpText = Text; collapsed when Text is empty. Status bar: `StatusBar.Net9`
// (".NET 9: installed" / ".NET 9: not installed (using compatible builds)", tooltip explains); the startup ".NET 9 not found" warning banner is gone.
```

- **Installer**: Velopack (`Velopack` NuGet 1.2.161 + `vpk` 1.2.161). `Program.Main` calls `VelopackApp.Build()...Run()` first; it acts only on
  `--veloapp-*` arguments, so `--computer-details` / `--persistence` jobs are unaffected. `build\make-installer.ps1` publishes self-contained win-x64
  and runs `vpk pack --packId EzForensics --packTitle "EZ Forensics" --shortcuts Desktop,StartMenuRoot` into `dist\` (`EzForensics-Setup.exe`,
  `EzForensics-Portable.zip`, `dist\releases\` = update feed files). Uninstall removes `%LOCALAPPDATA%\EzForensics` only, never the data folder.
- **Wizard UIA ids**: `Setup.Window`, `Setup.Page` (ItemStatus = Welcome|SystemCheck|Bundles|Summary|Install|Done; the License page is gone), `Setup.Next`
  (on Install after a run with problems: "Finish" -> Done), `Setup.Back`, `Setup.Skip`, `Setup.Guide`, `Setup.Welcome.Help`, `Setup.Welcome.DataRoot.Help`,
  `Setup.Check.<Windows|Disk|Admin|Wsl|Net9|Python|Internet|Installed>` (ItemStatus = short value, HelpText = level Ok|Info|Warning; `.Icon` = the fixed
  22x22 glyph slot, CheckRow.Glyph; `.Help` = "?" with the explanation; Python = Ok when found, Warning when missing),
  `Setup.Check.Again`, `Setup.Profile.Recommended|Everything` (no Custom radio; a manual tick selects neither), `Setup.Bundle.<id>` (+ `.Expand`
  (chevron; artifacts expanded by default), `.Size`, `.Help`, `.Selected` (one-line list of ticked items while collapsed)), `Setup.BundleCard.<id>`,
  `Setup.Item.<id>` (+ `.Note` (a few words; Zeek/RITA with WSL off: "Admin prompt + restart"), `.Help`; no `Setup.Item.ez-wsl-distro`: hidden),
  `Setup.Summary.Total|Admin|Admin.Help|Disk|Notes (the "?" with plan notes)|Steps`, `Setup.Licenses` + `.Accept` (Install disabled until ticked) `.Help`
  `.View` `.List`, `Setup.Install.Progress|Current|Elapsed|Steps|Failure|Retry|Cancel|LogExpander (expanded by default)|Log`,
  `Setup.Step.<stepId>` (ItemStatus = state, HelpText = message; the running step is scrolled to the middle of the list),
  `Setup.Done.Headline|Duration|Installed|AfterRestart|Failed|Restart|OpenEvidence` (OpenEvidence = the "Open EZ Forensics" button; id kept);
  removed: `Setup.ZeekRuntime` (+ `.Wsl|Docker`), `Setup.Check.Docker`, `Setup.DontShow`, `Setup.Profile.Custom`, `Setup.License.*`, `Setup.Done.Close`. Ingest / Tools "Tools not installed" banner:
  `FirstRun.Banner`, `FirstRun.OpenSetup`, `FirstRun.ExistingFolder`. `Settings.RunSetup`,
  `Settings.SetupGuide`, `Settings.OpenDataFolder`, `Settings.UpdateFeedUrl`, `Settings.CheckAppUpdate`, `Settings.InstallAppUpdate`,
  `Settings.UpdateStatus`; `Shell.Brand`, `Shell.Brand.Logo`.
- User guide: `docs\SETUP.md` (shipped next to the exe as `SETUP.md`).

## Navigation shell (nav footer, badges)
- `NavItem` is now an `ObservableObject` (`Title`, `Glyph`, `Page`, `HasAttention`, `IsBusy`, `BadgeTip`, `Tip`). `ShellViewModel.Items` still holds all six pages (so `AppServices.Navigate<T>()` / `NavigationRequested` by VM type keep working); `MainItems` (Triage, Tools, Jobs, Analyze) feed the list, `FooterItems` (Tools Configuration = ToolManagerViewModel, Preferences = SettingsViewModel) are reached through the gear menu.
- User-visible renames (class names unchanged): Results -> Analyze, Settings -> Preferences, Tool Manager -> Tools Configuration (`PageViewModel.Title` and page headers).
- Additive `AppServices.ResultsReady` event / `RaiseResultsReady()`: raised by `ResultsViewModel.OnCaseJobsSettled`; the shell also marks Analyze when any job reaches Succeeded. `HasAttention` clears when Analyze is opened. `IsBusy` on Jobs follows `RunningJobs > 0`.
- `ShellViewModel.ToggleThemeCommand` flips `AppSettings.Theme`, applies it, mirrors it into `SettingsViewModel.Edit.Theme` and saves.
- AutomationIds: `Shell.Nav.Theme`, `Shell.Nav.Gear`, `Shell.Gear.Preferences|Tools (Tools left the main nav; `Nav.Tools` is gone, opened from the gear menu; Run setup lives in Preferences > Setup)`, `Nav.Analyze` (was `Nav.Results`), `Nav.Badge.Attention|Running`, `Grid.ScrollTop|ScrollBottom`.
- Controls.xaml: new styles `ScrollArrowButton`, `ScrollEndButton`; scrollbars are 14 px with a CornerRadius 2 thumb (min length 32) and arrow buttons at both ends.

## Analyze case tree: Tool column, search, multi-select, file operations

- **Tool column** (after Name; in the header sort / resize / column picker as `Tool`): file = `CaseIndex.Describe(path).ToolId` (manifest, else the
  v2 file name / v1 tool folder; renamed files without a manifest entry show nothing), folder = `CaseTreeFilter.AggregateTool` (the one tool,
  "Mixed (n)" or ""). The tree reloads `CaseIndex` on every refresh (`ResultsViewModel.TreeCaseIndex`). Top-level layout v2 host folders get a
  computer glyph (`FileNode.IsHost`). Layout v2 case with v1 outputs (`HasLegacyOutputs`) → info line `Results.Tree.LegacyNote`.
  Tree items' ItemStatus is now `Earliest: …; Latest: …; Events: …; Size: …; Tool: …[; Hits: …]`.
- The tree never lists `logs` or dot-folders (`CaseTreeOrder.IsHiddenFolder`: `.ezstaging`, `.ezcache`); it lists truly empty folders (e.g. a new folder).
- **Search box** `Results.Tree.Search` (placeholder "Search files and contents…", Ctrl+F in the tree, Esc clears, `Results.Tree.Search.Clear`): file
  names filter at once; from `CaseTreeFilter.MinContentQueryLength` (2) characters `CsvKeywordSearch` runs over every listed CSV after 300 ms
  (a new keystroke cancels it), progress `Results.Tree.SearchProgress`, text `Results.Tree.SearchStatus`. Only matching files (name or content)
  and their folders (auto-expanded) stay visible; hit badge `CaseTreeFilter.FormatHits` ("1 hit", "1,234 hits", "10,000+ hits"). Clearing restores
  the previous expansion and reveals the open file. Opening a file with content hits sets `GlobalText` (grid search) to the keyword.
- **Multi-select** (`Views/Analyze/MultiSelectTreeBehavior`, attached `IsEnabled`; DataContext implements `IMultiSelectTree`): Ctrl+click toggles,
  Shift+click = range in visible order (`CaseTreeFilter.Range`), Ctrl+A = all visible, right-click on an unselected item selects only it.
  `FileNode.IsMultiSelected` drives the highlight. Selection bar `Results.Tree.SelectionBar` / `Results.Tree.SelectionText` with
  `Results.Tree.OpenCombined` and `Results.Tree.Merge` (from 2 items). Drag-rectangle selection is not implemented.
  Collapsing a folder drops its descendants from the multi-selection (`FileNode.OnIsExpandedChanged`; `TreeSelection` also ignores nodes under a
  collapsed folder, `FileNode.IsHiddenByCollapse`); the view calls `ResultsViewModel.OnTreeFolderCollapsed()` to refresh the selection bar.
- **Combined view** (several files, `CsvMerger.HaveSameSchema`): merged with `CsvMerger` (leading `SourceFile`, `Host` columns; host from
  `CaseIndex.HostOf`) into `<case>\.ezcache\combined\<CaseFileOperations.CombinedKey>.csv` (hidden; key = paths + sizes + mtimes; the 5 most recent
  kept) and opened like any CSV, so filters, sort, export and copy work unchanged. Info line `Results.Tree.CombinedInfo` ("Combined view of N files …
  Flags set here are not saved to the source files"; flags stay in the cache copy's sidecar). Different columns → warning
  "Selected files have different columns — use Merge into new file". Progress `Results.Tree.CombineProgress`.
- **Context menu** `Results.Tree.Menu` (items `Results.Tree.Menu.Open|OpenCombined|Merge|Cut|Copy|Paste|Duplicate|Rename|Delete|NewFolder|ShowInExplorer|CopyPath`);
  keys in the tree: Enter open, F2 rename, Del delete, Ctrl+C / Ctrl+X / Ctrl+V, Ctrl+D duplicate, Ctrl+Shift+N new folder, Ctrl+Shift+C copy path.
  Rename / New folder use `TextPromptWindow` (`Results.Prompt.Window|Text|Error|Ok|Cancel`). Copy also puts a FileDropList on the Windows clipboard.
  Delete asks (listing the items) and sends items + sidecars to the Recycle Bin (`Services/RecycleBin`: `SHFileOperation` with
  `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING`, owner = main window; an aborted operation / item still present throws). When the case is on a
  network share, mapped / removable / non-fixed drive (`CaseFileOperations.RecycleRefusal(path) != null`) Delete is refused with a message (Windows would
  delete permanently). Delete logs each item only after it was recycled; a failure after k items reports "Delete stopped after k of n item(s)…". The open file
  is closed (flags saved) before an operation that touches it, re-opened at its new path after rename / move; the tree refresh keeps the expansion.

```csharp
// Core/Case/CaseFileOperations.cs — every tree file operation; refusals throw CaseFileOperationException (InvalidOperationException).
public sealed class CaseFileOperations {
  const LogFileName = "file_operations.log", DerivedOutputsFileName = "derived_outputs.json", LogsFolderName = "logs", CacheFolderName = ".ezcache",
    CombinedFolderName = "combined", MergedName = "Merged"; const long MaxHashBytes = 256 MB;
  CaseFileOperations(string caseDir, Action<string, bool /*isDir*/> recycle, string? user = null /* DOMAIN\user */, Func<DateTime>? clock = null);
  string CaseDir, User, LogPath, DerivedOutputsPath; bool IsInsideCase(path); string Relative(full);
  string CheckSource(path, op) /* inside the case, not the case folder, not under logs\ .ezstaging\ .ezcache\, not EzForensics_manifest_*.json / EzHub_manifest_*.json / case.json /
    a sidecar, no protected file inside a folder, no reparse point (junction / symlink) at the path or any folder between the case and it */;
  string CheckDestinationDir(dir, op) /* same reparse-point rule */; static bool IsReparsePoint(path); static string? FirstReparsePointInside(dir);
  // Delete / Copy / Duplicate of a folder that contains a reparse point is refused ("contains a link…"; links are never followed or hashed through).
  // Move / Copy of a folder into the case root refuses CaseIndex.CaseLevelFolders names (as Rename / CreateFolder do).
  // CaseDir keeps the separator of a drive root (case at D:\ → CaseDir "D:\", LogPath "D:\logs\file_operations.log").
  static bool IsProtectedFileName(name); static string ValidateName(name, isCsv /* keeps .csv: appended */, isDirectory /* no dot-folders */);
  static string SanitizeOutputName(name) /* → ArtifactOutputLayout.IsValidOutputName: "my merged (2).csv" → My_merged_2.csv */;
  static IReadOnlyList<string> SidecarsOf(csv) /* <stem>.ezflags.json + every <name>.csv.* */; static string SidecarTarget(csv, sidecar, newCsv);
  static string UniqueTarget(path, isDir, sidecars?, sourceCsv?) /* Name_2, Name_3 (Name_2 → Name_3); sidecar targets must be free too */;
  string Rename(path, newName); string Move(path, destDir); string Copy(path, destDir); string Duplicate(path); string CreateFolder(parent, name = "New folder");
  void Delete(paths) /* all checked first; files hashed, recycled with their sidecars, then logged */; static string? RecycleRefusal(path, driveTypeOf?) /* UNC / non-fixed → reason */;
  Task<(string Path, CsvMergeResult)> MergeAsync(inputs, outputPath, progress?, ct) /* inside the case; name sanitised; numbered; recorded */;
  void RecordDerivedOutput(DerivedOutputRecord); static IReadOnlyList<DerivedOutputRecord> ReadDerivedOutputs(caseDir);
  static string DefaultMergePath(caseDir, host?) /* <case>\<SafeHostFolder(host) | Merged>\Merged\Merged_<n>.csv */;
  static string CombinedCacheDir(caseDir); static string EnsureCombinedCacheDir(caseDir) /* .ezcache Hidden */; static string CombinedKey(paths);
  static void PruneCombinedCache(caseDir, keep, except?); void Log(op, from, to, sha256); static string HashForLog(path) /* sha256 | skipped:>256MB | - */; }
// logs\file_operations.log: one line per file: "<UTC yyyy-MM-ddTHH:mm:ss.fffZ>\t<user>\t<op>\t<from>\t<to>\t<sha256|->", op = RENAME | MOVE | COPY |
//   DUPLICATE | NEWFOLDER | DELETE | MERGE-INPUT | MERGE. Files are hashed when copied / deleted / merged (≤ 256 MB).
// logs\derived_outputs.json: JSON array (camelCase) of DerivedOutputRecord { kind "merge", output (case-relative), outputSha256, rows,
//   inputs [{ path (case-relative), host, sha256 }], createdUtc, user, app "EZ Forensics" }.
public static class CaseTreeFilter { const int MinContentQueryLength = 2; FormatHits(count, capped, culture?); NameMatches(name, query);
  HashSet<T> Visible<T>(roots, children, isMatch) /* matches + ancestors */; AggregateTool(tools); List<T> Range<T>(visibleOrder, anchor, target); }
public static class CaseTreeOrder { ...; static bool IsHiddenFolder(string name) /* logs, .* */; }
```

## Analyze grid: flag cycling, column menu, Actions menu, focus mode
- Flag cell click cycles none -> each flag in `FlagCatalog` order (Informational -> Interesting -> Suspicious -> Malicious -> Benign by default; user-editable list of stable ids in `AppSettings.Flags`, `FlagCatalog.Next(list, id?)`) -> none. In a multi-row selection the clicked row's next flag is applied to all selected rows (`ResultsViewModel.CycleFlag`). `ToggleFlagCommand` cycles one row. Ctrl+N (Nth flag) / Ctrl+0 / Space unchanged. A non-null flag becomes `Settings.FlagLastId`.
- Column header right-click menu (`Results.Menu.Header.Hide|ShowAll|Columns|BestFit|BestFitAll|ResetWidths`); "Columns…" popup `Results.Columns.Popup` with `Results.Columns.All|None` and `Results.Columns.Item.<csv column index>`. The flag column cannot be hidden and at least one data column stays visible.
- `AppSettings.ColumnLayouts` (`Dictionary<string, ColumnLayout>`, key = `ColumnLayoutStore.Signature(headers)`, max 200, least recently used dropped): `Hidden` CSV column indices and `Widths` (only non-default widths). Hidden columns still take part in filtering. Export (filtered / flagged) always writes ALL columns; Copy rows writes only the visible columns.
- Top bar: `Results.Actions` button (menu `Results.Actions.Menu`) with `Results.Export`, `Results.ExportFlagged` (ids kept), `Results.Actions.CopySelected|CopyFlagged|CopyFiltered|OpenDefaultApp|ShowInExplorer|ResetColumnWidths`. Copies are tab-separated with a header row (`TsvFormatter`), ask Yes/No above 100,000 rows, and stop at 64M characters (the user is told). `AppSettings.EscapeFormulasOnCopy` (default true; Preferences checkbox `Settings.EscapeFormulasOnCopy` under Results flags) prefixes cells starting with `= + - @` (not plain numbers) with `'` (`TsvFormatter.Clean(v, escapeFormulas)`, `IsFormulaLike`, `Build(..., escapeFormulas)`); CSV exports are never changed. A copy that wrote fewer rows than requested because the file was closed mid-copy reports "Copy incomplete: only n of m …". Kept: `Results.FlagFilter`, `Results.FlaggedCount`.
- Focus mode: `Results.FocusMode` toggle button, F11 toggles, Esc exits. `AppServices.FocusModeChanged(bool)` / `IsFocusMode` / `SetFocusMode(bool)`; `ResultsView` collapses LeftCard/PaneSplitter/columns; `ShellViewModel.IsFocusMode` zeroes `NavColumnWidth` and `MainWindow` hides `NavPanel` (entering stores NavPinned / NavExpanded and collapses the overlay; exiting restores both — a restored unpinned overlay closes again via the leave timer when the mouse is elsewhere). Leaving Analyze (shell selection change or view unload) exits focus mode.

## Reliability advisories UI (Analyze)
- Banner above the grid (`ResultsViewModel.AdvisoryBanner` = `AdvisoryBannerViewModel`, `Views/Analyze/AdvisoryBanner.xaml`): AutomationIds `Results.Advisory.Banner|Icon|Headline|Summary|Details|Dismiss|HidePermanently|Item.{AdvisoryId}|Item.{AdvisoryId}.Link`.
- Column header marker: `Results.Header.Advisory.{Column}` (HelpText = warn|crit|info|ok, Name = tooltip).
- Override rules: `%LOCALAPPDATA%zForensicsDatadvisories.json` (AppPaths.DataRoot). Pure shaping: `Core/Advisories/AdvisoryPresentation.cs`.

## Page rename: Triage -> "Ingest" (user-visible only)

The Triage page is labelled **"Ingest"** in the UI (`src/EzForensics.App/Services/PageNames.cs`: `public const string Ingest = "Ingest";`, used by the ShellViewModel NavItem,
`TriageViewModel.Title`, the page header and every "Triage page" / "Triage finished" message). User-visible renames only, class names unchanged: `TriageViewModel`,
`Core/Triage`, `Core/Resources/triage` and every `Triage.*` AutomationId stay. `Nav.{Title}` becomes `Nav.Ingest`. README, docs/SETUP.md and docs/LINUX_PARSERS.md use "Ingest".

### Run selected never fails silently (Core/Triage/TriageRunCheck.cs)

```csharp
public sealed record RunGate(int PlannedCount, int SelectedCount, string? SystemErrorText, bool CaseRunActive, bool MemoryPlanning, bool CaseFolderEmpty);
public sealed class RunSkips { int ZeekNotSetUp, MemoryNotSetUp, MissingToolJobs; List<string> ZeekErrors, RitaNotes, MissingTools; bool Any; }
public static class TriageRunCheck {
  string? BlockedReason(RunGate g);   // null = Run selected enabled; otherwise the tooltip / hint text (always non-empty)
  IReadOnlyList<string> MissingTools(IEnumerable<JobSpec>, IToolLocator, BuildFlavor? preferred = null);
  string? ToolMissing(JobSpec, IToolLocator, BuildFlavor? preferred = null); // ExeName not resolved (or resolved file gone); rooted ExePath missing; bare ExePath not checked
  string Describe(int queued, RunSkips skips, string caseDir);              // "Queued N job(s)…" / "Nothing was queued: <reason>" (never an empty reason)
}
```

- `TriageViewModel.CanRunSelected() == (RunBlockedReason == null)`; the Run button shows `RunButtonToolTip` (ShowOnDisabled) and the line under the
  grid (`Triage.RunStatus` / `Triage.RunHint`, below the Planned jobs) shows the disabled reason or the last run outcome (`RunStatus`, `RunHintSeverity`).
- Every Run selected outcome goes through `ReportRun` = banner + the line next to the button + `app.log` (`Ingest run: …`); the click itself is
  logged (`Ingest: Run selected clicked (…)`). Exceptions: `Could not start the ingest run: …` (Error) with the log path.
- Selected jobs whose tool is missing are **not queued** (after one `Locator.Rescan(ToolsRoot)`): the message names the exes and `Open setup`
  (`Triage.RunOpenSetup`, `GoDownloadToolsCommand`) is shown.
- Admin jobs while not elevated: no separate card. The one run hint (`Triage.RunStatus`, `x:Name=RunHintBar`, docked directly **below** the
  Planned jobs grid) says how many jobs need Administrator and shows the choice buttons (`Triage.AdminWarning` panel, visible while
  `ShowAdminWarning`): `Triage.RestartAsAdmin`, `Triage.SkipAdminJobs`, `Triage.RunAnyway`, `Triage.AdminCancel`. It is scrolled into view
  (`AdminWarningRequested`). Cancel clears the hint (nothing queued).
- Restart as Administrator keeps the page: `TriageViewModel.CaptureSession()` → `IngestSessionStore.Save` (Core/Triage/IngestSessionSnapshot.cs,
  `%LOCALAPPDATA%\EzForensicsData\ingest-session.json`: source, case name / folder, scan result (`TriageScanResult` + `PcapScanResult`),
  archive state, card ticks and host labels, include-pagefile, every row's wanted tick by `PlannedJobItem.KeyOf`; never a password), then
  `Elevation.RestartAsAdministrator(IngestSessionLaunch.Args(path))` = `--restore-ingest "<path>"` (UAC declined: the file is deleted, the hint
  says so). The elevated start waits up to `IngestSessionLaunch.MutexWait` (30 s) for the old instance's `Local\EzForensics.App` mutex
  (`SingleInstanceGuard.TryBecomeFirst(TimeSpan)`), `IngestSessionStore.Take` reads and always deletes the file (ignored when older than
  `MaxAge` = 1 h or another `Version`), and `RestoreSessionAsync` applies it on the Ingest page **without rescanning** (hint: "Restarted as
  Administrator: the Ingest page was restored …"). Only a file named `ingest-session.json` is ever read from the switch.

## Memory (Volatility 3)

Volatility 3 runs natively on Windows from its own venv based on the plaso `PrivatePython` (no WSL). Memory images are detected by magic bytes (the extension only
decides whether a file gets opened), planned on the Ingest page like any artifact and written to `<case>\<Host>\Memory\`.

```csharp
// Core/Memory
public enum MemoryFormat { Unknown, WindowsCrashDump, Hibernation, LiME, VMware, ElfCore, Raw }
public enum MemoryOs { Unknown, Windows, Linux, Mac }
public enum DumpKind { Full, Kernel, Bitmap, BitmapFull, BitmapKernel, Active }   // crash dumps only (DumpType 1 full, 2 kernel, 5 bitmap; SDMP = kernel, FDMP = full)
public sealed record MemoryImage(string Path, long Size, MemoryFormat Format, MemoryOs Os, DumpKind? DumpKind, long? PhysicalPages, string? Note)
  { string? CompanionPath /* .vmem next to a .vmss/.vmsn */; }   // Note e.g. "user-mode pages missing" for kernel dumps
public static class MemoryImageDetector { static MemoryImage? Detect(string path);   // null = not a memory image; reads only headers + a bounded 64 MB scan for raw images
  /* PAGEDU64/PAGEDUMP, hibr/HIBR/wake/RSTR (file name hiberfil.sys), EMiL, .vmss/.vmsn magic 0xBED2BED2/0xBED3BED3/0xBAD1BAD1 + sibling .vmem, ELF e_type 4;
     raw (.raw .mem .vmem .lime .dmp, no header): size >= 256 MB, multiple of 4096, kernel marker ("KDBG"/"ntoskrnl.exe") or "Linux version " in the first 64 MB.
     Never: .bin/.img, .dmp < 64 MB, MDMP minidumps, pagefile.sys, swapfile.sys. */ }
public static class MemoryImageScanner { static IReadOnlyList<MemoryImage> Scan(string root, PcapScanOptions? options = null, CancellationToken ct = default);
  /* reuses the PcapScanOptions limits + SkipDirectories (a file root is probed directly); also looks at root-level hiberfil.sys, MEMORY.DMP and Windows\MEMORY.DMP */ }
public static class MemoryHostHint { static string? TryGetComputerName(string imagePath, long maxBytes = 4L << 30, CancellationToken ct = default);
  /* stream-scans for UTF-16LE "COMPUTERNAME=", majority value; HostNameSource = MemoryHostHint.FromMemoryStrings = "memory strings" */ const string FromMemoryStrings = "memory strings"; }
// Host name order: (1) image under a discovered system root -> that host, (2) MemoryHostHint, (3) file stem. After the run the registry.printkey job
// (ControlSet001\Control\ComputerName\ComputerName) records the authoritative name: a mismatch adds a note, the folder is never renamed.

// Core/Memory/Volatility*.cs
public sealed record VolatilityInstall(string Exe /* <venv>\Scripts\vol.exe */, string Python, string? Version, string Root, string SymbolsDir, string CacheDir);
public sealed class VolatilityLocator { VolatilityLocator(IPlasoProbe? probe = null, string? root = null);
  static string ManagedRoot /* AppPaths.ComponentRoot("volatility", "VOLATILITY_ROOT") */; VolatilityInstall? Discover(string? configuredPath);   // configured -> managed venv -> PATH
  string? ProbeVersion(VolatilityInstall) /* python -c "import importlib.metadata as m;print(m.version('volatility3'))" */; }
public static class VolatilitySetup { const string PinnedVersion = "<newest verified release, >= 2.26.x with hibernation support>";   // set at implementation time
  const string SymbolPackUrl = "https://downloads.volatilityfoundation.org/volatility3/symbols/windows.zip";
  /* venv on PrivatePython; pip install volatility3==<PinnedVersion> pefile capstone pycryptodome yara-python (never [full]) */
  static IReadOnlyList<string> PipInstall(VolatilityInstall); static Task<Job> RunStepAsync(JobQueue, SetupStepContext/step, ...); static IReadOnlyList<string> Uninstall();
  /* PlasoSetup.Uninstall must not delete plaso\python while the Volatility venv exists (guard) */ }
public static class VolatilityCommandBuilder { static VolatilityInvocation Build(VolatilityInstall install, string imagePath, MemoryPluginDef plugin,
    bool offline, string? extraArgs = null, bool includePagefile = false, string? pagefilePath = null /* appended; null = pagefile.sys next to the image,
    or in <root> for <root>\Windows\MEMORY.DMP; never the running system's own C:\pagefile.sys */);
  static string? FindPagefile(string imagePath); static IReadOnlyList<string> SplitArgs(string); static string ToDisplayString(VolatilityInvocation);
  /* -r csv -f <image> --symbol-dirs <install.SymbolsDir> --cache-path <install.CacheDir> [--offline] [--single-swap-locations <pagefile>] <plugin> [args]; no -q;
     environment PYTHONNOUSERSITE=1 (+ PYTHONIOENCODING=utf-8, PYTHONUNBUFFERED=1); argv only (ArgumentList) */ }
public sealed record VolatilityInvocation(string Exe, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Environment);
public sealed record MemoryPluginDef(string Id, string Plugin, string Label, string Variant, string ArtifactId, bool DefaultSelected, string Os /* windows|linux */,
  IReadOnlyList<string> Args, bool NeedsUserPages, int TimeoutBaseMin, int TimeoutPerGbMin) { bool IsInfo /* Plugin == "windows.info" */; }
public static class MemoryPlugins { const string InfoPlugin = "windows.info"; static IReadOnlyList<MemoryPluginDef> All /* embedded, cached */;
  static MemoryPluginDef? Find(string id); static IReadOnlyList<MemoryPluginDef> ForOs(MemoryOs); static IReadOnlyList<MemoryPluginDef> Load(Stream json); }
public static class VolatilityProgressParser { static double? Parse(string stderrLine);   // "Progress: 45.12  Scanning ..." -> 45.12 (0..100); the UI parses Job.Output lines
  static string? Status(string stderrLine) /* text after the percentage */; }
public sealed record VolatilityCsvResult(IReadOnlyList<string> Headers, long Rows, int BannerLinesStripped, long TimestampsConverted);
public static class VolatilityCsv { const string FirstHeader = "TreeDepth";
  static VolatilityCsvResult Normalize(string rawCsvPath, string outCsvPath /* same path = in place */, MemoryPluginDef? plugin = null, CancellationToken ct = default);
  static VolatilityCsvResult Normalize(TextReader, TextWriter, MemoryPluginDef? plugin = null, CancellationToken ct = default);
  static bool IsHeaderLine(string); static bool TryConvert(string value, bool timeColumn, out string formatted);
  /* strips the banner / any lines before the CSV header (no header = InvalidDataException), undoes Python csv backslash escapes and rewrites RFC 4180 (CRLF);
     "yyyy-MM-dd HH:mm:ss.ffffff UTC" / "+hh:mm" values (any column) and bare ones (time columns: header contains time|date|created|updated|timestamp|written)
     -> TimelineTimestamp.OutputFormat UTC; N/A, -, None in time columns -> empty */ }
public sealed class MemorySymbolsMissingException : Exception;   // thrown by WaitAsync after Fail
public sealed class MemorySymbolGate { MemorySymbolGate(); MemorySymbolGate(int maxConcurrent); const int MaxConcurrent = 2; const string SymbolsMissingMessage;
  Task WaitAsync(CancellationToken ct) /* throws MemorySymbolsMissingException(Message) when failed */; void Open(); void Fail(string message) /* first decision wins */;
  bool IsOpen; bool Failed; string? Message; static bool IsSymbolError(string? line);
  Task AcquireSlotAsync(Job, CancellationToken); void ReleaseSlot(Job); int Running;   // soft cap of 2 vol processes per image (taken in PreProcess, released in Finally)
  /* appended (review fixes): bool IsDecided; long DecidedSequence; void Rearm(Job infoJob) /* an info job queued after the decision starts a new check */;
     bool FailedBefore(Job) /* the failure predates the job (Retry / Run again): the plugin runs instead of failing fast */; Job.QueueSequence orders both */
  /* one per image; windows.info warms the symbol cache and opens/fails the gate (in its PostProcess, before hashing the image; Finally opens it as a fallback after a
     cancel/timeout/early failure); other plugin jobs await it in PreProcess and fail fast with the same message ("Preparation failed: <SymbolsMissingMessage>")
     ("Unsatisfied requirement"/symbol_table_name in stderr -> plain text: connect to the internet, install the symbol pack, or Import ISF...).
     The gate starts closed: when no info job is run for an image, the planner MUST call Open() itself or the plugin jobs wait for ever. */ }
public static class MemoryJobs { const string ToolId = "Volatility3", Category = "Memory", OutputName = "Memory", RuleId = "memory";
  static JobSpec CreateSpec(VolatilityInstall install, MemoryImage image, MemoryPluginDef plugin, string host, string outputDir, string stagingDir, MemorySymbolGate gate,
    bool offline, string? qualifier = null, bool includePagefile = false, string? pagefilePath = null, string? extraArgs = null /* last two appended */);
    // stagingDir = per-image base: the job's own staging folder is PluginStagingDir(stagingDir, plugin) = "<stagingDir>_<Variant>" (jobs never share one);
    // StdoutPath = <that folder>\<Variant>.csv; Output = OutputPlan(<that folder>, "Memory", plugin.ArtifactId, qualifier); Provenance { RuleId "memory", ArtifactId, Host,
    // Inputs [image], HashInputs false }; PreProcess = gate wait + slot; PostProcess = VolatilityCsv.Normalize in place (+ info: gate decision, input.sha256);
    // Finally = slot release, gate fallback, *_PARTIAL.csv normalised. Details: engine, volatility.version, volatility.plugin, symbols.mode, symbols.dir, input.path,
    // input.size, image.format, memory.os, dump.kind, image.note, image.userPagesMissing, input.pagefile, volatility.rows, input.sha256 (info).
  static string PluginStagingDir(string stagingDir, MemoryPluginDef); static string VolatilityInputPath(MemoryImage) /* .vmem companion for VMware */;
  static TimeSpan TimeoutFor(MemoryPluginDef plugin, long imageBytes) /* base + perGB * size */; static IReadOnlyList<MemoryPluginDef> LoadPlugins() /* Resources/memory/plugins.json */; }
// Memory waiting reasons + no-infinite-wait gate (appended): JobSpec gains a last parameter Action<Job>? Enqueued (called synchronously by JobQueue.Enqueue
//   before the job can start; exceptions ignored). JobQueue.WaitingForSlotReason = "Waiting for a free job slot" (a PrepareOutsideSlot job after its PreProcess).
//   MemorySymbolGate: void RegisterInfo(Job) (the info spec's Enqueued hook: tracks the job and Rearm()s at once, so plugins queued after it wait for the new check);
//   IReadOnlyList<Job> InfoJobs; bool HasLiveInfoJob() (an info job of the current check whose Recorded is not complete); void ReportInfoProgress(Job, line);
//   static string? StageOf(line) ("downloading symbols 12%", "finding the kernel 45%", "reading symbols 0%", "running 3%"); string WaitingStatus();
//   Task WaitAsync(Action<string>? onStatus, CancellationToken) (polls every PollInterval = 1 s; when no live info job is seen for OrphanGrace = 5 s the gate
//   opens itself with DecisionNote NoInfoJobNote / InfoEndedNote: never waits for ever); Open(Job? by, string? note) / Fail(string, Job? by) (ignored when `by`
//   is older than the info job that armed the current check); string? DecisionNote; consts WaitingForInfoReason = "Waiting for windows.info (symbol check)",
//   WaitingForSlotReason; static FormatElapsed(TimeSpan). Memory plugin jobs: StatusReason "Waiting for windows.info (symbol check): windows.info is queued |
//   <stage>… | running…", then "Waiting for a free Volatility slot (at most 2 per image)", then JobQueue.WaitingForSlotReason; log when the wait ends:
//   "# <note> — starting <plugin> (waited N s)." (note "windows.info finished", "windows.info was cancelled; starting without the symbol check", ...) or
//   "# Symbol check failed after waiting N s: <message>". MemoryTriage.Plan opens the gate when no info spec was actually built for the image.
// Memory follow-ups: JobSpec.PrepareOutsideSlot (memory jobs set it): PreProcess runs WITHOUT a queue slot; meanwhile State=Queued, StatusReason=JobQueue.WaitingReason ("Waiting…"),
//   StartUtc null; a slot is re-taken afterwards and StartUtc/Running begin then (durations measure the real run). Other tools unchanged.
//   VolatilityProgressThrottle (queue use for ToolId Volatility3): "Progress:" lines reach the log file only on stage change, +5 %, 100 %, or every 2 s; blank lines dropped
//   (live Job.Output still gets every line). JobOutcomeClassifier.CountCsvRows counts quote-aware CSV records (streaming). MemoryJobs.DropEmptyPartialCsvs: a cancelled/timed-out
//   memory job publishes no header-only *_PARTIAL.csv (log: "cancelled before any rows were produced"). Timeline source types may set rowKeyColumn/rowKey/valueColumn
//   (only rows whose rowKeyColumn cell equals rowKey emit events; a timestamp entry may omit column to use valueColumn): memory-info -> "Memory acquired (system time)".
//   Crash dump support: Volatility 3 reads DumpKind Full/Bitmap/BitmapFull/BitmapKernel only; Kernel (type 2) and Active (type 6) plan no jobs (MemoryTriage.UnsupportedDumpNote);
//   the "user-mode pages missing" note applies to Kernel and BitmapKernel, not Active.
// Core/Triage
public static class MemoryTriage { const string RuleId = "memory", OutputName = "Memory";
  static IReadOnlyList<PlannedJob> Plan(IReadOnlyList<MemoryImage> images, string caseDir, VolatilityInstall? install, IReadOnlyDictionary<string, IReadOnlyList<string>>? selectedPluginIds = null,
    string? sourceRoot = null, IReadOnlyList<DiscoveredSystem>? systems = null, bool offline = false);   // like NetworkTriage.Plan: placeholder jobs when Volatility is missing
  static bool IsPlaceholder(JobSpec); static JobSpec Resolve(PlannedJob, VolatilityInstall); }
// Core/SystemInfo
public sealed class MemoryDetailsProvider : IComputerDetailsProvider   // reads Memory_Info*.csv + the crash-dump header PhysicalMemoryBlock NumberOfPages;
  { void Contribute(SystemDetails s, string systemRoot, CancellationToken ct); }   // MemoryUnknown -> "≈N GB usable (from memory image)", adds a "Memory image" category;
                                                                                    // a memory-only host gets ComputerDetails.json written by the info job's PostProcess
// Ordering (follow-up): the Ingest page's post-run steps (computer details, memory-only ComputerDetails.json, timeline auto-build) wait for every run job's
//   Job.Recorded (see Core/Execution below) so the windows.info manifest record (image.format, input.sha256) is on disk before MemoryDetailsProvider reads it.
```

- **plugins.json** (`Core/Resources/memory/plugins.json`, embedded): `{ "version": 1, "plugins": [ { "id", "plugin", "label", "variant", "artifactId", "defaultSelected",
  "os" (windows|linux), "args": [], "needsUserPages", "timeoutBaseMin", "timeoutPerGbMin" } ] }`. Default selected: windows.info, pslist, pstree, psscan, cmdline, netscan,
  svcscan, dlllist, malfind, registry.printkey (computer name), and for the process correlator cmdscan, consoles, verinfo, hollowprocesses, processghosting,
  suspicious_threads, pebmasquerade. Optional (unticked): netstat, handles, filescan, getsids, privileges, envars, ldrmodules, sessions,
  registry.hivelist, registry.userassist, amcache, modules, driverscan, threads, getservicesids, joblinks, timeliner. `os: linux` entries are planned only when a matching ISF exists.
  Plugin names checked against volatility3 2.28.2 (the pinned version): malfind and ldrmodules use the non-deprecated `windows.malware.malfind` /
  `windows.malware.ldrmodules`, amcache is `windows.registry.amcache`; the computer name is `windows.registry.printkey --key ControlSet001\Control\ComputerName\ComputerName`
  (id `computername`, variant `ComputerName`). Linux: `linux.pslist`, `linux.pstree`, `linux.bash` (ids `linux-*`, variants `Linux*`). Ids are lower-case, artifact
  id = `memory-<id>`. `VolatilityPluginProbe` (`vol -h`, cached per vol path and version in `<cache>\ezforensics-plugins.txt`) marks plugins that the install does not list as unavailable (fails open). No hibernation job: 2.28.2 has no `windows.hibernation` plugin (hiberfil.sys is detect-and-flag only).
- **JobSpec appended parameter** `string? StdoutPath = null` (after `Provenance`): when set, the process's raw stdout is written to that file byte for byte (no log
  lines, no encoding changes); only stderr goes to the job log / `Job.Output`. The file lives in the staging folder and is published like any output (the
  PostProcess normalises it). Null = unchanged behaviour (stdout is logged).
- **Output naming row** (`ArtifactNaming.RenameTable`): `("Volatility3", @"^(?<v>.+)$", "{base}_{V}", "{default}")`. The artifact id is the plugin's
  `artifactId` carried in `OutputPlan.ArtifactId` (one plugin per job), because `{v:id}` would kebab-case the variant (`PsList` -> `ps-list`, not `pslist`).

  | Rule (tool) | Category | Output name(s) | Artifact id(s) |
  |---|---|---|---|
  | memory (Volatility3, per image + plugin) | Memory | `Memory_<Variant>[_<imagestem>].csv` (`Memory_Info`, `Memory_PsList`, `Memory_NetScan`, ...) | memory-<id> (memory-info, memory-pslist, ...) |

  The `_<imagestem>` qualifier is used for a host's second and later images (`TriagePlanner.UniqueQualifier`). Category "Memory" is added to `CaseIndex.Categories`.
  Cancelled / timed-out plugin runs publish `Memory_<Variant>_PARTIAL.csv`.
- **Setup**: `SetupStepKind.InstallVolatility`, `SetupStepKind.InstallVolatilitySymbols` (appended). Reuses the `ProvisionPython` step (deduplicated with plaso). The Memory bundle and
  the volatility3 item lose `ComingSoon` (`DefaultOn` true in Everything, false in Recommended; RITA stays ComingSoon). Optional item "Windows symbol pack (offline)":
  `DefaultOn=false`, downloads `VolatilitySetup.SymbolPackUrl` into the symbols directory. `SystemCheckResult.VolatilityVersion` (appended).
- **Settings** (append-only): `string? VolatilityPath`, `bool VolatilityOffline` (true adds `--offline`).
- **Linux/macOS images**: detected and flagged only; "Import ISF..." in Tools Configuration copies JSON(.xz) into the symbols directory.
- **Timeline** (`memory-*` source types): pslist CreateTime "Process created" `...B` and ExitTime "Process exited"; psscan the same, not included by default; netscan Created
  "Socket created"; sessions CreateTime; userassist LastUpdated; timeliner Created/Modified/Accessed/Changed Date as B/M/A/C with message "{Plugin}: {Description}";
  info SystemTime "Memory acquired"; malfind, cmdline, dlllist, svcscan ignored.
- **Advisories**: artifacts `memory-process`, `memory-network`, `memory-carved`, `memory-timeliner`; rules point-in-time (info), smear (warning, raw/live images),
  carved objects (warning, psscan/netscan/filescan: stale objects or false positives), kernel dump (user pages missing).
  Memory artifacts are identified by file name / manifest artifact id only (psscan has exactly pslist's columns and netscan netstat's, so headers cannot tell them apart).
  Crash dumps: `MemoryImageDetector.ReadBugCheckCode(path)` / `IsLiveDumpBugCheck(code)` (0 = header made by an acquisition tool, 0x161 LIVE_SYSTEM_DUMP = live);
  the memory jobs record details `dump.bugcheck` (`0x00000080`) and `memory.live` (`true|false`), `CsvProvenance.MemoryLiveDump` carries it and
  `AdvisoryContext.MemoryLiveDump` (bool?, appended) suppresses the smear warning for a crash dump written at a real bug check (`false`); null = cautious (warn).
  A live-written dump also gets the image note "written from a running system (bug check code 0x…)".
  The info job's status on missing symbols is "Exited with code 1: " + `SymbolsMissingMessage` (not Volatility's raw requirement error).
- **Timeline** additions: netstat `Created` "Socket created" (`memory-netstat`); the computer-name job (`memory-computername`) is ignored.
- **FakeTool**: `FakeVolatilityTool` handles `-r csv -f <img> ... <plugin>` (banner + fixture CSV on stdout, `Progress:` lines on stderr); env switches FAKE_VOL_HANG,
  FAKE_VOL_NOSYMBOLS (exit 1, "Unsatisfied requirement"), FAKE_VOL_EXIT.
- **Ingest UI**: a "Memory images" card (count/size, Volatility status, "Set up Volatility") and one plan group per image with plugin checkboxes. Hibernation conversion
  (`windows.hibernation.Dump`, best effort) and "include pagefile" (`--single-swap-locations`, when pagefile.sys is in the same system root) are optional and unticked.
  A single-file source that is a memory image is accepted.

## Archives (archived evidence on the Ingest page)

An archive source is hashed, inspected, extracted (password asked from the user when encrypted) into `<case>\.ezstaging\extracted\<sha8>\<sanitised stem, max 40>\`, and the
extracted root(s) replace the source everywhere (`ArtifactScanner`, `PcapScanner`, `MemoryImageScanner`, the manifest's `sourceRoots`,
`Timeline.SetDefaultPlasoSources`, the computer-details job). Several roots are always passed as a list, never as their common parent (`<case>\.ezstaging\extracted`
may hold extracted copies of other archives from earlier ingests): the manifest records `sourcePath` = the path the user picked (archive file or folder) and
`sourceRoots` = this ingest's extracted roots (`JobQueue.SetSource(path, roots)`); plaso runs one log2timeline per root into one storage file.
`EffectiveSource` (single root or common parent) remains for display / capture-relative paths only. The archive is read-only evidence (`FileAccess.Read`, `FileShare.Read`; size and mtime are recorded before and after).

```csharp
// Core/Archives (SharpCompress behind IArchiveEngine; fall back to SharpSevenZip if the spike shows 7z AES / RAR5 -hp do not decrypt)
public enum ArchiveFormat { Zip, SevenZip, Rar, Tar, GZip, TarGz, Vhd, Vhdx, Unknown }
public static class ArchiveFormatDetector { static ArchiveFormat Detect(string path);
  /* magic bytes: PK\3\4 zip, 7z\xBC\xAF\x27\x1C, Rar!\x1A\x07\x00|\x01, "ustar" at 257 tar, 1F 8B gz (tar inside -> TarGz), "conectix" vhd, "vhdxfile" vhdx; extension only a hint */ }
public sealed record ArchiveEntryInfo(string Name /* as stored */, long Size, long CompressedSize, bool IsDirectory, bool IsEncrypted, bool IsSymlink, long? Crc32,
  DateTime? ModifiedUtc, DateTime? CreatedUtc, DateTime? AccessedUtc, EntryTimeSource TimeSource);
public enum EntryTimeSource { None, DosLocal, ExtendedTimestamp /* 0x5455 */, NtfsExtra /* 0x000a */, SevenZipHeader, TarPax, TarMtime }
public sealed record ArchiveInspection(string Path, ArchiveFormat Format, bool IsEncrypted, bool HeadersEncrypted, string? EncryptionMethod /* "ZipCrypto" | "AES-256" | ... */,
  long TotalUncompressedSize, int EntryCount, IReadOnlyList<ArchiveEntryInfo> Entries /* empty when HeadersEncrypted and no (correct) password */, bool IsSolid);
public interface IArchiveEngine { string Name; string Version;
  ArchiveInspection Inspect(string path, string? password = null);          // lists entries without extracting; encrypted headers -> HeadersEncrypted = true (never throws for that)
  bool VerifyPassword(string path, string password);                         // fully decodes the smallest encrypted entry (first entry of the first solid block for 7z) and checks its CRC
  Stream OpenEntry(string path, ArchiveEntryInfo entry, string? password);   // forward-only stream of the decoded entry; the caller disposes it
  IEnumerable<(ArchiveEntryInfo Entry, Stream Data)> EnumerateEntries(string path, string? password, CancellationToken ct); }   // solid archives: one sequential pass
public sealed record ArchiveLimits { int MaxEntries = 1_000_000; long FreeSpaceMarginBytes = 1L << 30; double PromptRatio = 200; long PromptRatioMinBytes = 1L << 30;
  int MaxNestingDepth = 2; int MaxPathLength = 240; }   // injectable for tests; per-entry output may never exceed the declared size
public enum ArchivePhase { Hashing, Inspecting, Password, Extracting, RestoringTimes, Scanning }
public sealed record ArchiveProgress(ArchivePhase Phase, long BytesDone, long BytesTotal, int EntriesDone, int EntriesTotal, string? CurrentEntry);
public sealed record ArchivePasswordRequest(string ArchivePath, ArchiveFormat Format, string? EncryptionMethod, bool WrongPassword /* previous attempt failed */);
public enum TimestampCoverage { ModifiedOnly, All, Mixed, DosLocal }
public sealed record ArchiveOrigin(string ArchivePath, string Sha256, ArchiveFormat Format, bool Encrypted, string? EncryptionMethod, string ExtractedTo,
  IReadOnlyList<string> Chain /* outer -> inner archive paths for nested archives; just [ArchivePath] otherwise */, TimestampCoverage TimestampCoverage);
public sealed record ExtractionRecord /* .ezextract.json in the extracted folder (camelCase) */ { int Version = 1; string ArchivePath, Sha256; ArchiveFormat Format; bool Encrypted;
  string? EncryptionMethod; string Engine, EngineVersion; DateTime ExtractedUtc; bool Complete; long ArchiveSize; DateTime ArchiveModifiedUtc; TimestampCoverage TimestampCoverage;
  string? Layout /* "kape" | "velociraptor" | null */; string? CopyLogPath; IReadOnlyList<ExtractionEntry> Entries; IReadOnlyList<string> Warnings; }
public sealed record ExtractionEntry(string OriginalName, string ExtractedPath /* relative to the extracted folder, "" when skipped */, long Size, bool CrcOk, string? Sha256,
  bool TimesRestored, string? TimeSource /* EntryTimeSource name */, string? Reason /* skipped (symlink, traversal, bomb) or renamed (percent-encoded, ~2 case duplicate, hash-shortened) */);
public sealed record ArchiveIngestResult(IReadOnlyList<string> ExtractedRoots, ArchiveOrigin Origin, ExtractionRecord Record, IReadOnlyList<string> Warnings);
public static class ArchiveIngest { static Task<ArchiveIngestResult> PrepareAsync(string archivePath, string caseDir,
    Func<ArchivePasswordRequest, Task<string?>> passwordProvider /* null = user cancelled -> OperationCanceledException */, IProgress<ArchiveProgress>? progress,
    CancellationToken ct, ArchiveIngestOptions? options = null /* IArchiveEngine, ArchiveLimits, keep = true */);
  /* SHA-256 first (reuse key); same SHA + Complete = true -> reuse without a password prompt; incomplete -> wipe and redo; free space check (declared total + 1 GB);
     cancel removes the partial folder; nested container archives (depth <= 2, only entry or no system root found) auto-extracted, the same password is tried first;
     .vhd/.vhdx: no extraction, a warning telling the user to mount it (needs admin) and pick the drive */ }
public static class ArchivePaths { static string? Sanitize(string entryName, out string? reason);
  /* '/' and '\' normalised, '.' dropped, '..' refused, roots (/, C:\, \\srv) stripped; reversible percent-encoding of invalid chars and ':' (file%3AZone.Identifier),
     reserved device names (NUL.txt) and trailing dots/spaces; $MFT kept; final check GetFullPath(root+rel) starts with root\; case-only duplicates get ~2;
     paths over 240 chars shorten the component with a hash suffix; symlinks/hardlinks are never created (recorded as skipped) */ }
// Core/Triage
public static class ArtifactScanner { ...; static TriageScanResult ScanSources(IReadOnlyList<string> sources, rules? = null, IHiveOpener? = null, int maxDepth = 3,
    CancellationToken ct = default, Func<string,bool>? isRunningOsRoot = null, IReadOnlyDictionary<string, ArchiveOrigin>? origins = null /* source root -> origin: its systems get Archive + AcquisitionKind.ExtractedArchive */);   /* ScanSource for each root, results merged, host names kept unique across roots */ }
```

- **Engine implementation (as built)**: `SharpCompressArchiveEngine` (SharpCompress 0.50.5 for zip / 7z / RAR; `System.Formats.Tar` for tar / tar.gz; `GZipStream`
  for single-file .gz), plus `ArchiveInspector` (RAR -hp header flags, method names), `ArchivePasswordVerifier`, `ZipExtraFieldReader` (own central-directory parser:
  NTFS 0x000a, 0x5455 mtime + local-header atime, AES 0x9901, unix symlink mode), `EntryPathSanitizer` (stateful: ~N duplicates, hash shortening, containment; wraps
  `ArchivePaths.Sanitize`), `ArchiveExtractor`, `TimestampRestorer`, `VelociraptorLayout` / `KapeLayout` (`IExtractionLayout`), `ExtractionRecord.Load/Save`.
  Appended to the contract shapes above (all optional / append-only): `EntryTimeSource.RarHeader`, `EntryTimeSource.GZipHeader`; `ArchiveEntryInfo.Index` (`int = -1`);
  `ArchiveLimits.MaxTotalOutputBytes` (`long.MaxValue`); `ArchiveIngestOptions { Engine, Limits, Keep = true, Confirm, FreeSpace, Passwords }` with
  `Func<ArchiveConfirmRequest, Task<bool>>? Confirm` (`ArchiveConfirmKind { LowDiskSpace, HighCompressionRatio }`; false = OperationCanceledException; null = low space
  fails / high ratio continues with a warning) and `ArchivePasswordSession` (memory-only list of passwords the user typed; nested and later archives try it first);
  `ArchiveIngestResult.Origins` (one `ArchiveOrigin` per extracted root); `ExtractionRecord.Chain`. Exceptions: `ArchiveIngestException`, `ArchiveWrongPasswordException`,
  `ArchiveLimitExceededException` (no password in any message). For a sole nested container the result's `ExtractedRoots`/`Origin` are the inner archive's (Chain outer → inner).
  Appended (review fixes): `ArchiveOrigin.Parent` (`ArchiveOrigin?`, the archive a nested one came from; the manifest records every parent in `Archives`),
  `ArchiveOrigin.SelfAndParents()`, `ArchiveOrigin.ForPath(origins, path)`, `ArchiveProvenance.Attach(spec, inputPath, origins)` (jobs with no discovered system, e.g. a
  memory image alone in an archive, get their archive by path). The ingest caps `MaxTotalOutputBytes` at the free space minus the margin (64 MB reserve when the user
  continued on low space), entries never overwrite or delete an existing file (8.3 alias: skipped with a warning), `.ezextract.json.tmp` is reserved too, and a complete
  copy of a different archive with the same sha8 + stem is never wiped (the new one goes to `<stem>~<sha 9-16>`).
  Notes: SharpCompress exposes only mtime for 7z / RAR, so those are `ModifiedOnly`; encrypted RAR CRCs are HMAC-converted and not checked (`Crc32 = null`);
  `ArchivePaths` also percent-encodes '%' inside a component that needs encoding (keeps it reversible); extracted files are only opened by name-suffix (an archive
  extension) when looking for nested containers, so restored last-access times are not disturbed. VHD/VHDX: not hashed, no folder created, one warning.
- **Password prompt and cancel**: the provider passes the scan's token: `ArchivePasswordWindow.Ask(owner, request, CancellationToken ct = default)` closes itself
  (returns null, PasswordBox cleared, no result kept) when the token fires, e.g. Cancel on the Ingest page while the prompt is open.
- **Password rule**: the password exists in memory only, for the session. It is never persisted (settings, manifest, `.ezextract.json`), never logged, never in exception text and
  never on a process command line. There are **no common-password attempts** (no "infected"/"malware" quick-try, no setting for it): when `Inspect` reports `IsEncrypted` the app
  asks the user (`ArchivePasswordWindow`, PasswordBox, shows archive name / format / encryption method), verifies with `VerifyPassword`, shows "Incorrect password" and allows unlimited
  retries; Cancel is always available. The manifest records only `encrypted: true` and the method.
- **Layouts**: KAPE zip extracted as is (`*_CopyLog.csv` recorded in the extraction record). Velociraptor `Collection-*.zip` (client_info.json or collection_context.json plus
  uploads/): percent-decode components, map `C%3A` / `%5C%5C.%5CC%3A` to `C`, merge auto/ntfs/file accessors (ntfs wins, duplicates recorded) into
  `<root>\<Hostname>\C\...`; shadow copies to `<Host>\_velociraptor\vss\...`, results/logs to `<Host>\_velociraptor\`. A single-file .gz extracts to a folder with one file.
  Archives inside a system tree (e.g. `Users\x\Downloads\a.zip`) are evidence: listed, never auto-extracted. A folder with several archives shows "N archives found - Extract and scan".
- **Timestamps**: restored after each file closes, directories bottom-up at the end. mtime always; created/accessed only when the archive stores them (NTFS extra 0x000a, 0x5455, 7z
  header times, tar pax; never tar ctime as Created). DOS-only zip time = local time, unknown zone, 2 s precision (`TimeSource` DosLocal).
- **Provenance** (all appended; old manifests load unchanged): `AcquisitionKind.ExtractedArchive` (after `CopiedCollection`; the enum is persisted as text); `DiscoveredSystem.Archive`
  (`ArchiveOrigin?`, init, default null); `JobProvenance.Archive` (`ArchiveOrigin?`); `ManifestSource` gains `ArchivePath`, `ArchiveSha256`, `ArchiveFormat`, `ArchiveEncrypted`,
  `ExtractedTo`, `ArchiveChain` (`List<string>?`), `TimestampCoverage` (text); `RunManifest.Archives` (`List<ManifestArchive>?`: ArchivePath, Sha256, Format, Size, ModifiedUtc,
  Encrypted, EncryptionMethod, ExtractedTo, Chain, TimestampCoverage, Engine, EngineVersion). Advisories: `EvidenceAcquisition.ExtractedArchive` (`CopyHeuristics` treats it as a declared copy),
  trigger `archiveExtracted` with rules `archive-times-mtime-only` (warning) and `archive-times-restored` (info) for `*` with knownArtifactOnly.
- **Settings** (append-only): `bool KeepExtractedEvidence` (default true). The Ingest page has "Open extracted folder" and "Delete extracted copy".
- **UI ids** (suggested): `Triage.Archive.*` (card), `ArchivePassword.Window|Text|Error|Ok|Cancel`.
- **Tests**: archives are generated in the tests (plain zip, a raw-zip writer for traversal / `:` names / NUL.txt / case duplicates / 0x000a / lying sizes / ZipCrypto, tar/tgz with `../` and
  symlinks, zip-in-zip, Velociraptor layout, zip bomb against small `ArchiveLimits`); fixtures for AES zip, 7z AES (plain and encrypted headers), RAR4 and RAR5 -hp live in
  `tests/TestData/archives` with a README.

## RITA (Core/Rita): beaconing, long connections, DNS tunneling over Zeek logs

RITA v5.1.2 (Active Countermeasures, **GPL-3.0**) runs inside the private EZ Forensics WSL distro (`EzDistro.Name` = "EzForensics"), never in a user distro.
Pins (resolved 2026-10-11, `RitaRelease`): image index `ghcr.io/activecm/rita@sha256:a2bb0ef6185e33780dbc3ce7d86e38ac7a65c98e729e17510fe29717eb34b76a`;
amd64 manifest `sha256:c53629801508…48b9b`, layer `sha256:f3f8820ca289…25286` (14,575,853 B), `/rita` SHA-256 `d82d51a9fdf5…4c21` (29,631,327 B); arm64 manifest
`sha256:30a82a74ed80…9b9a5`, layer `sha256:b919e7a2fbb3…0f5fa`, `/rita` `4025ba99f73c…912bb` (full values in `RitaRelease`). ClickHouse 24.1.6.52 static build from the GitHub
release (`clickhouse-common-static-24.1.6.52-<arch>.tgz`, SHA-512 pinned from its `.sha512` asset; amd64 260,679,829 B). `http_extensions_list.csv` (required by rita at start-up)
from the source at v5.1.2, SHA-256 pinned. Measured install (amd64): ≈265 MB download, 510 MB in `/opt/ezhub/rita` (clickhouse ≈480 MB, rita 30 MB); first install 45 s,
re-install / update 5 s (a verified clickhouse of the pinned version is kept).

```csharp
// Core/Rita
public enum RitaEngine { Wsl, Docker /* reserved: CreateSpec / Build throw NotSupportedException(RitaSetup.DockerNotSupportedMessage) */, Direct /* fake tool in tests */ }
public sealed record RitaInstall(RitaEngine Engine, string Exe /* wsl.exe | fake */, string? RitaVersion, string? ClickHouseVersion, string? Distro = null)
  { ManifestDetails() /* rita.version, clickhouse.version, rita.engine, rita.image */; Display; }
public sealed record RitaOptions { IReadOnlyList<string> InternalSubnets /* DefaultInternalSubnets = RFC 1918 + fd00::/8 */; bool OnlineThreatIntel /* false */;
  static Default; static FromSettings(AppSettings); static NormalizeSubnets(IEnumerable<string?>) /* split on , ; whitespace; valid CIDRs only */; static string? NormalizeCidr(string?); }
public sealed record RitaCaptureLogs(string Capture, IReadOnlyList<string> LogFiles /* Zeek_Conn_x.log or conn.log ... */);
public sealed record RitaStagedLog(string Folder /* c01.. */, string Name /* conn.log */, string Capture, string SourcePath);
public static class RitaRelease { Version = "5.1.2", Tag, Image, IndexDigest, ImageReference, Amd64 / Arm64 / Pin (RitaImagePin), ClickHouseVersion = "24.1.6.52",
  ClickHouseAmd64 / Arm64 / ClickHouse (ClickHousePin), HttpExtensionsUrl / Sha256, DownloadMB = 265, DiskMB = 520, License = "GPL-3.0", LicenseNotice /* UI text */; }
public static class RitaLinks { Website, Source, SourceAtTag, License, Releases, ImagePackage, ConfigDocs, ClickHouse, ClickHouseLicense; }
public static class OciBlobFetcher { GetTokenAsync; GetManifestAsync (digest-verified); DownloadBlobAsync (SHA-256 + size); ExtractEntry(tgz, entry, dest) /* that file only */;
  VerifyBinary(file, pin); Task<string> FetchRitaAsync(HttpClient, RitaImagePin, dir, IProgress<double>?, Action<string>?, ct) /* anonymous ghcr.io token, no Docker */; }

public enum RitaSetupOption { None, Install, Update, InstallDistroThenRita /* EzDistro.EnsureAsync first */, NeedsWsl }
public sealed record RitaDiscovery(RitaInstall? Selected, EzDistroInfo Distro, string? Marker, string? RitaVersion, string? ClickHouseVersion, long? DataBytes,
  RitaSetupOption Setup, string? Note) { bool Installed; bool UpdateAvailable; string Summary; }
public static class RitaLocator { RitaDiscovery Discover(IRuntimeProbe? = null) /* blocking; one sh -c in the distro */; FromProbe(info, output) /* pure */;
  ParseRitaVersion / ParseClickHouseVersion / MarkerPart; }

public static class RitaSetup {
  Root = "/opt/ezhub/rita", BinDir, EtcDir, RitaBin, ClickHouseBin, ClickHouseConfig, ClickHouseUsers, RitaConfig, MarkerPath (etc/installed),
  DataDir = "/var/lib/ezhub/clickhouse", WorkRoot = "/var/lib/ezhub/rita", LockFile, TcpPort = 19000, HttpPort = 18123, SchemaVersion = 1, MarkerText;
  ToolIdInstall = "rita-install", ToolIdUninstall = "rita-uninstall", ToolIdDeleteDatabases = "rita-delete-databases"; DockerNotSupportedMessage; DownloadDir;
  ClickHouseConfigXml /* 127.0.0.1 only, 19000 / 18123, max_server_memory_usage_to_ram_ratio 0.5, small caches, no system log tables, UTC */; ClickHouseUsersXml; RitaEnv;
  string RitaConfigHjson(RitaOptions?) /* internal_subnets, online_feeds only when OnlineThreatIntel, update_check_enabled false */;
  ProbeScript; InstallScript(ritaWslPath, options?) /* re-runnable */; UninstallScript; DeleteDatabasesScript /* flock: refuses while an analysis runs */;
  RuntimeStep InstallStep(wslExe, ritaWslPath, options?) / UninstallStep(wslExe) / DeleteDatabasesStep(wslExe);
  Task<(bool Ok, string Message)> InstallAsync(HttpClient, Func<RuntimeStep, CancellationToken, Task<Job>> runStep, Action<string> log, RitaOptions? = null,
    IProgress<double>? = null, IRuntimeProbe? = null, ct) /* needs EzDistro ready; fetches + verifies rita on Windows, installs as a job; Install = Update */;
  UninstallAsync(runStep, probe?, ct) /* program files + every database */; DeleteDatabasesAsync(runStep, probe?, ct); }

public sealed record RitaInvocation(string Exe, IReadOnlyList<string> Args, IReadOnlyList<string>? CancelCommand, IReadOnlyDictionary<string, string>? Environment);
public static class RitaCommandBuilder {
  InputsFile = "rita_inputs.tmp", ViewFile, PairsFile, SniFile, DnsFile, SpanFile, InfoFile (work files); ScriptMarker = "EZFORENSICS-RITA-PIPELINE"; ViewLimit = 1_000_000;
  bool IsValidDatabaseName(string?); string DatabaseName(caseDir, host? /* null = case-wide */) /* ez_<case>_<host|case>_<hash6>, ≤ 63, lower-case, no hyphen */;
  string? OriginalLogName(path) /* Zeek_Conn_x.log -> conn.log, Zeek_OpenConn_x.log -> open_conn.log; null for logs RITA ignores */;
  IReadOnlyList<RitaStagedLog> Stage(IEnumerable<RitaCaptureLogs>) /* one c01.. folder per capture */; InputsText / ParseInputs;
  PairsSql / SniSql / DnsSql / SpanSql (FORMAT CSVWithNames; RITA v5.1.2 tables conn, openconn, http, openhttp, ssl, openssl, dns, threat_mixtape; zeek_uid = MD5(uid));
  string PipelineScript(wslWorkDir, database, RitaOptions?) /* LF only */; WslCancelArgs(database); RitaInvocation Build(RitaInstall, workDir, database, RitaOptions?); }
public sealed record RitaConversion(Files, Threats, Beacons, LongConnections, DnsTunneling, double? DataSpanHours, FirstUtc, LastUtc, NoData, RitaVersion,
  ClickHouseVersion, Database, MissingSpans);
public static class RitaResultConverter { ThreatsFile = "Rita_Threats.csv", BeaconsFile, LongConnectionsFile, DnsTunnelingFile, OutputFiles, UpstreamColumns;
  RitaConversion Convert(workDir, outputDir? = null, captures? = null, ct); FormatHours(double) /* invariant "12.5" */; CoveredHours(intervals); ReadRecords(text) /* RFC 4180 */; }

public sealed record RitaJobRequest { required string CaseDir; string? Host /* null = case-wide */; string? OutputDir; IReadOnlyList<Job> ZeekJobs /* may still be queued */;
  IReadOnlyList<RitaCaptureLogs> Captures /* existing logs */; RitaOptions Options; long InputBytesHint /* e.g. total pcap bytes */; string? Label; JobProvenance? Provenance; }
public static class RitaJobs { ToolId = "Rita", Category = "Network", RuleId = "rita", OutputName = "Rita", DefaultArtifactId = "rita-threats", CaseWideHost = "_Case",
  LegacyFolder = "Rita"; TimeoutFor(connLogBytes) /* 20 min + 30 min/GB */; JobTimeout(hint) /* TimeoutFor(hint) + 15 min; 6 h without a hint */;
  OutputDirFor(caseDir, host?) /* v2: <case>\<Host or _Case>\Network; v1: <case>\Network\Rita\<Host or _Case> */; StagingDirFor; WorkDirFor (staging + ".work");
  RitaCaptureLogs? LogsOf(Job zeekJob); WaitForZeekAsync(jobs, status, ct); JobSpec CreateSpec(RitaInstall, RitaJobRequest); DeleteWorkDir(dir); }
```

- **Job**: `CreateSpec` gives a `PrepareOutsideSlot` PreProcess that waits (status "Waiting for n of m Zeek job(s)") until every Zeek job is recorded, keeps the succeeded ones
  (`rita.capturesSkipped` notes the rest), fails with "none of the N Zeek job(s) produced logs RITA can read" when nothing is left, and writes the input list into the work folder
  (`<case>\.ezstaging\Rita_<host|Case>_<hash6>.work`, deleted when the job ends). One process: `wsl -d EzForensics -u root -e sh -c <PipelineScript>`: flock (one analysis at a
  time; the others log "waiting"), links the logs under their original names in `/var/lib/ezhub/rita/run/<db>/logs/cNN/`, starts `clickhouse server` (a trap stops it),
  `timeout <#timeout> rita import --database <db> --logs … --rebuild` (every capture lands in one bucket; never `--rolling`), `rita view --stdout --limit 1000000`, the enrichment
  queries. Exit codes: 2 no input, 3 not installed, 70 ClickHouse, 71 import, 72 view, 124 import timeout. "could not find imported data" (no internal-to-external traffic)
  is a success with header-only CSVs and `rita.note`. CancelCommand: `pkill` the script (marker + db), rita and clickhouse (verified: nothing left running).
  Databases stay after a run (re-runs use `--rebuild`); Tool Manager "Delete RITA databases" = `DeleteDatabasesAsync`.
- **Outputs**: `Rita_Threats.csv` (all findings), `Rita_Beacons.csv` (beacon score > 0 or strobe), `Rita_LongConnections.csv` (long connection score > 0), `Rita_DnsTunneling.csv`
  (C2 over DNS score > 0); artifact ids `rita-threats`, `rita-beacons`, `rita-long-connections`, `rita-dns-tunneling` (rename row `Rita ^Rita_(?<v>.+)$ -> {base}_{V}`, new-name
  pattern `^Rita_<V>(_…)*.csv`). Columns: `DateTimeUtc` (= FirstSeenUtc), `FirstSeenUtc`, `LastSeenUtc` (yyyy-MM-dd HH:mm:ss.fffffff UTC; empty when unknown), `Severity`, `Src`,
  `Dst` (empty for "::"), `Fqdn`, then RITA's columns unchanged (Beacon Score, Strobe, Total Duration, Long Connection Score, Subdomains, C2 Over DNS Score, Threat Intel, Prevalence,
  `First Seen (relative)`, Missing Host Header, Connection Count, Total Bytes, Port:Proto:Service, Modifiers), `Captures` ("a.pcap; b.pcap"). Header-only when empty.
  First / last seen come from ClickHouse: per (src, dst) over conn / http / ssl (+ open_*), per (src, fqdn) over http.host / ssl.server_name, per DNS domain over the dns queries
  (sub-domains included).
- **Manifest details**: `rita.version`, `clickhouse.version`, `rita.engine`, `rita.image`, `rita.db`, `rita.scope` (host | case), `rita.captures` ("a.pcap | b.pcap"), `rita.capturesSkipped`,
  `rita.internalSubnets`, `rita.onlineThreatIntel`, `rita.connLogBytes`, `rita.dataSpanHours` (invariant number: hours covered by the union of each capture's first..last),
  `rita.firstSeenUtc`, `rita.lastSeenUtc`, `rita.findings` ("threats=11,beacons=11,longConnections=0,dnsTunneling=0"), `rita.note` (no data).
- **Case-wide folder**: `_Case` sits at host level (`<case>\_Case\Network`); `CaseIndex.FromPath` currently reads it as host "_Case" (the triage / UI agents decide whether to show it
  as a case-level node).
- **Settings** (append-only): `bool RitaEnabled` (default true; runs only when RITA is installed), `List<string> RitaInternalSubnets` (default RFC 1918 + fd00::/8),
  `bool RitaOnlineThreatIntel` (false).
- **Ingest planning** (`Core/Triage/NetworkTriage`): `IsRita(PlannedJob)`, `IsCaseWideRita(PlannedJob)`; `PlanRita(zeekPlan, caseDir, RitaInstall?)` = one row per host with Zeek
  rows (`<case>\<Host>\Network`) + one case-wide row (`<case>\_Case\Network`, Artifact.MatchedPath / Provenance.Host = `_Case`) when ≥ 2 captures; always placeholders
  (ToolId "Rita", no ExePath), ticked only when RITA is installed. At run: `ZeekJobsFor(rita, (planned, job) of this run)` (host match; case-wide needs ≥ 2) and
  `ResolveRita(planned, install, caseDir, zeekJobs, RitaOptions)` → `RitaJobs.CreateSpec` (same OutputDir / label / provenance; InputBytesHint = the captures' sizes).
  The Ingest page enqueues RITA rows after every Zeek job of the run; rows whose Zeek jobs are unticked or with RITA not installed are skipped with a message.
- **App**: `AppServices.Rita` / `IsProbingRita` / `RitaChanged` / `EnsureRitaProbed()` / `RefreshRitaAsync()` / `RaiseRitaChanged()` (settings changes). Ingest network card:
  `Triage.Rita`, `Triage.RitaEnabled`, `Triage.RitaStatus`, `Triage.SetUpRita`, `Triage.RitaSubnets`, `Triage.RitaEditSubnets`. Tools Configuration card `ToolManager.Rita`
  (`.Status .Headline .Install .Update .Cancel .Detect .DeleteDatabases .Uninstall .Step .Progress .Version .Pinned .Engine .Location .Data .Enabled .Note .DockerNote
  .Subnets .SaveSubnets .ResetSubnets .SubnetsValidation .ThreatIntel .License .Links .LinkSource .LinkLicense .JobState .Log`; `.ViewJob` removed, install output goes to the app log).
- **Docker**: not implemented (`RitaSetup.DockerNotSupportedMessage`); the two-container variant (rita + clickhouse/clickhouse-server:24.1.6 on a private network, same pins) is a follow-up.
- **Fake tool**: `EzForensics.FakeTool --fake-rita --out <workDir> --db <db> --subnets <cidrs>` (RitaEngine.Direct) reads the input list and writes the same work files from the Zeek
  conn / dns logs; environment FAKE_RITA_HANG, FAKE_RITA_EXIT, FAKE_RITA_NODATA, FAKE_RITA_NOSPANS.
- **Measured** (EzForensics distro, 4 vCPU, 12 GB): PCAP1.pcapng (264 MB, 5.6 min of traffic): Zeek 12-16 s, RITA job 7-9 s (ClickHouse start < 1 s, import 3 s); 11 SNI beacon
  findings (severity Low / None: Microsoft update and telemetry hosts, beacon score up to 0.73). The lab C2 at 10.0.2.15 is internal under the default subnets; with internal
  subnets `192.168.227.0/24` it shows as 192.168.227.140 -> 10.0.2.15 (5 connections in one minute, beacon score 0.19, severity None): short captures under-detect.

## UI polish round (Oct 2026, branch app-ui-polish)

Supersedes the earlier sections where they disagree.

- Jobs page: `JobsViewModel.Rows` is oldest first (queue order). `ScrollRequested` (select + scroll) and `FollowRequested` (scroll only: the job that starts running while nothing is selected). Grid columns are pixel widths (Output `*`, MinWidth 200); the global DataGrid template binds `HorizontalScrollBarVisibility` / `VerticalScrollBarVisibility` (style default Auto).
- Super timeline: plaso only. Removed `SuperTimelineBuilder`, `TimelineScanner`, `TimelineMappings`, `TimelineSort`, `TimelineMacb`, `TimelineModels`, `Resources/timeline/mappings.json`, `TimelineEngine`. `Core/Timeline/TimelineFiles` keeps `TimelineFolderName` / `OutputPrefix` (CaseTreeOrder still pins `SuperTimeline_*.csv`, incl. old `SuperTimeline_EZ*.csv`). `AutoBuildEngine.EzCsv` is kept for loading old settings and reads as `Off`; default `Off`. Removed AutomationIds: `Triage.Timeline.Engine|Refresh|Sources*|IncludeGeneric|CombineMacb`.
- Memory plugins: `AppSettings.VolatilityPlugins` (null = catalog defaults), `MemoryPlugins.SelectedFor(os, configured)` (windows.info always). Edited on the Volatility card (`ToolManager.Volatility.Plugins|PluginDefaults|PluginsAll|Plugin_<id>`); Ingest plans every image with it (`AppServices.MemoryPluginsChanged`). Removed `Triage.MemoryImage.Include|Plugins|PluginDefaults|PluginsAll`, `Triage.MemoryPlugin_*`.
- Tools Configuration deep link: `AppServices.OpenToolsConfiguration(ToolsSection)` navigates and scrolls to the card (`ToolsSection { Plaso, Zeek, Rita, Volatility }`).
- Ingest page: source box + `Triage.Browse` (menu `Triage.BrowseFolder` / `Triage.BrowseFile`); drop a file / folder on the page or paste a path (quotes stripped, Explorer file lists accepted). Removed `Triage.Drive`. Planned jobs (`Triage.Planned`) follow the Scan row; scan details are one line each below the list (`Triage.Systems` expander, `Triage.ArchiveCard`, `Triage.EvidenceArchives`, `Triage.Network`, `Triage.MemoryCard`, `Triage.Persistence`, `Triage.Notes`) with links to Tools Configuration (`Triage.SetUpZeek|SetUpVolatility`). Removed `Triage.RitaEnabled|RitaSubnets|RitaEditSubnets|SetUpRita` (RITA on/off and subnets: Tools Configuration).
- Analyze: page title "Analyze"; InfoBar is one line with a More/Less toggle (`InfoBar.More`) for long messages; first-run banner, legacy-layout note, combined-view info, advisory summary, flag and filter help are one line with a "?" tooltip.

## Tools tab round (Oct 2026, branch b2-tools-tab)

- **Cards** (`Core/Catalog/ToolGroups.cs`): `enum ToolGroup { Disk, Memory, Network, Timeline }` in this order (was System, Network, Memory, Timeline, Persistence, Utilities). Disk glyph U+EDA2 (HardDrive); unknown catalog categories fold into Disk. Card AutomationIds `Tools.Card.Disk|Memory|Network|Timeline` (`Tools.Card.System|Persistence|Utilities` retired).
- **Utilities removed**: bstrings, Hasher, VSCMount, EZViewer and TimelineExplorer JSONs deleted (catalog = 17 CLI + 5 GUI explorers); the "utilities" / "viewers" / "liveresponse" category mappings, `SetupCatalog.NotInSetup`, `AppSettings.TimelineExplorerPath` (+ Settings row), `AppServices.LaunchTimelineExplorer` are gone. "Open in Timeline Explorer" became "Open in default app" (`ShellOps.OpenFile`): `Results.Actions.OpenDefaultApp` (was `OpenTimelineExplorer`), `Triage.Timeline.OpenInDefaultApp` (was `OpenInTimelineExplorer`); `ResultsViewModel.OpenInDefaultAppCommand`, `SuperTimelineViewModel.OpenInDefaultAppCommand`.
- **Zeek panel**: `Tools.ZeekPanel` visible only when `ToolsViewModel.ShowZeekPanel` (= `ZeekReady && IsZeekSelected`). It is the only panel in the special-tool grid (the cards are `ToolManagerView` sections).
- **Plaso options** now in `AppSettings`: `PlasoParsers` (string), `PlasoFormat` (PlasoOutputFormat), `PlasoWorkers` (string, empty = auto), `PlasoTimezoneUtc` (true), `PlasoVss` (false), `PlasoUnattended` (true), `PlasoFromDate` / `PlasoToDate` (DateTime?, date part kept). `PlasoOptionsViewModel` (App) is a view over them (saves on every edit; `Apply(PlasoRunOptions)` fills a run); `ToolManagerViewModel.PlasoOptions` feeds the Timeline-page card `ToolManager.Plaso.Options` (`.Parsers .Format .Workers .Utc .Vss .Unattended .From .To`), shown with the plaso card. `SuperTimelineViewModel` forwards its old properties to `Options` (Ingest controls `Triage.Timeline.Plaso*` unchanged) and builds with `PlasoOptionsViewModel.Apply`.
- **Maintenance queue**: Tools-tab installs, updates, pulls, uninstalls (plaso, Zeek, RITA, Volatility steps via `*Setup.RunStepAsync(S.Maintenance.Queue, ...)`, log folders `S.Maintenance.NewLogDir(name)`) and the EZ `--sync` jobs (`ToolManagerViewModel.SyncMaps`, and a Tools form run with `--sync`) run on `AppServices.Maintenance`; process output goes to `AppLog` only, the cards keep their own status log lines. `AppServices.HasActiveJob(prefixes)` checks both queues (card install/uninstall guards). `S.Queue` holds only parsing / analysis jobs.

## Ingest jobs round (Oct 2026, branch b3-ingest-jobs)

Supersedes the earlier sections where they disagree.

- **Browse**: one button `Triage.Browse` (`BrowseSourceCommand`), no menu; `Dialogs.PickFileOrFolder` is an `OpenFileDialog` with `ValidateNames=false`, `CheckFileExists=false` and the name `Dialogs.SelectFolderPlaceholder` ("Select this folder"): an existing file or folder is returned as is, the placeholder as its folder. Removed `Triage.BrowseFolder`, `Triage.BrowseFile`, `BrowseSourceFileCommand`, `TriageView.Browse_Click`.
- **Step rows** (`Core/Triage/IngestSteps.cs`): `ComputerDetailsRuleId` ("ingest-computer-details", tool `ezforensics-computer-details`, label "Computer details (offline registry)") and `SupertimelineRuleId` ("ingest-supertimeline", tool `plaso`, label "Supertimeline (plaso)", output `<case>\Timeline`). `Compose(jobs, caseDir, sources, plasoAvailable)` = Computer details first, the tool jobs, the super timeline last (only with plaso); no step without tool jobs or without a source folder (a single evidence file has none). `Order(items, planned)` keeps that order (used by `TriageViewModel.SetPlan`). `Sources(p)` = the row's `Spec.Args` (the source folders: the extracted archive roots, else the source folder). Both are ticked by default.
- **Run order**: Computer details is queued first (`ComputerDetailsJob.Enqueue` now returns every job queued, one per system), then the tool jobs, then RITA after Zeek; no job reads the computer details output, so nothing waits for it. When every job of the run has finished and its manifest record is written, the memory-only computer details are written (only when the Computer details row was ticked) and the super timeline starts (`TriageViewModel.BuildSupertimelineAsync`, `PlasoTimelineRunner` on `S.Queue` with `PlasoOptionsViewModel.Apply`), then the extracted-copy cleanup is scheduled. `IsBuildingTimeline` blocks Run selected and Delete extracted copy.
- **Removed**: `AppSettings.AutoBuildSuperTimeline`, `AppSettings.AutoBuildEngine`, `enum AutoBuildEngine` (old settings files still load; the keys are dropped on save). `SuperTimelineViewModel` (deleted; `ChoiceItem<T>` moved to `PlasoOptionsViewModel.cs`), `TriageViewModel.Timeline`, `RunJobs`, `RunDone`, `RunTotal`, `HasRun`, `RunProgress`, `RunSummary`, `ViewLogCommand`, `OpenJobOutputCommand`, `SelectNoneCommand`. AutomationIds `Triage.Timeline*` (whole panel: `.AutoBuild .PlasoStatus .InstallPlaso .PlasoSource .PlasoBrowseFolder .PlasoBrowseImage .Browse .CaseFolder .Options .PlasoParsers .PlasoFormat .PlasoWorkers .PlasoUtc .PlasoVss .PlasoUnattended .From .To .Build .Cancel .ViewLog .Progress .ProgressText .Result .OpenInResults .OpenInDefaultApp .OpenFolder`), `Triage.RunSummary`, `Triage.RunList`, `Triage.JobState`, `Triage.JobReason`, `TriageJobViewLog_{n}`, `TriageJobOpenOutput_{n}`.
- **Planned jobs** (`Triage.Planned`): buttons `Triage.SelectionMenu` (Custom selection drop-down: saved Ingest selections, then Manage… which opens Preferences > Ingest selections; items `Triage.Selection.<name>`), `Triage.SelectAll` (All), `Triage.SelectClear` (Clear, `ClearSelectionCommand`), then `Triage.Run`. The detected systems card (`Triage.Systems`, expanded) is at the top of the planned jobs, no longer in the Details expander. New columns: *Progress* (`Triage.Planned.Progress`: hidden before the run, empty while queued, indeterminate while running, full when done) and *Result* (a state badge: rounded `Chip` border coloured by `PlannedJobItem.StatusSeverity` via `DynamicResource Brush.Success/Error/Warning/Info` (+`*Bg`), styles `ResultBadge` / `ResultBadgeText`; while the row has a job the badge is the button `Triage.Planned.Result` (Name = status text), `OpenRowJobCommand` → `AppServices.ShowJob`; without a job (Waiting / Skipped) a non-clickable badge whose text is `Triage.Planned.Status`, shown when `ShowStatusBadge`). The top `Triage.RunStatus` / `Triage.RunHint` line stays.
- **PlannedJobItem** (own file): `AttachJob(job)`, `ResetRun()`, `Jobs`, `Job` (the representative job: running, else queued, else the first that did not succeed, else the last; `JobResultText.RepresentativeIndex`), `IsWaiting` (super timeline before it starts), `IsBusy` (plaso between its jobs), `NotQueuedReason` (ticked but not queued: shown as Skipped with the reason as tooltip), `ShowProgress`, `ProgressIndeterminate`, `ProgressValue`, `Status`, `StatusToolTip`, `HasJob`, `IsComputerDetails`, `IsSupertimeline`. Job state changes are raised on the UI thread.
- **Result text** (`Core/Execution/JobResultText.cs`): `Text(state, exitCode)` = Queued / Running / Done / Failed (exit N) / Failed / Timed out / Cancelled / Skipped.
- **Jobs page**: the Output column is 400 px wide (no star column), so widening columns shows the horizontal scroll bar. *Open manifest* and `JobsViewModel.OpenManifestCommand` are removed (`JobQueue.GetManifestPath` stays: the tests use it). Nothing installs or sets up on `S.Queue` any more (only `S.Maintenance.Queue`); `S.Queue` runs Ingest jobs, Tools runs (non-`--sync`), the Tools Zeek panel, computer details, Retry and the plaso super timeline.

## Ingest cards round (Oct 2026, branch w3-ingest-cards)

Supersedes the earlier sections where they disagree.

- **Defaults** (`Core/Triage/IngestDefaults.cs`): `FastForensicsRuleIds` = SANS fast-forensics triage set (FOR498 / FOR508, KAPE `!SANS_Triage`): `ingest-computer-details`, `mft`, `usnjrnl`, `prefetch`, `amcache`, `appcompat`, `evtx`, `registry`, `lnk`, `jumplists`, `shellbags`. `IsDefault(p)`: those rows keep their own default, memory / pcap / rita rows keep theirs (they exist only for detected, checked artifacts), every other row (super timeline, srum, sqlite-hunt, recyclebin, activitiescache, sum, recentfilecache, persistence, imported rules) is off. `IngestSteps.Compose` applies it to every row (`IngestDefaults.Apply`); `IngestSteps.Supertimeline` is `SelectedByDefault = false`. The initial tick after Scan uses the saved "Default" Ingest selection (`IngestDefaults.UseDefaultSelection`); `Triage.SelectionMenu` ticks exactly the rows whose rule id is in the picked selection (`IngestSelection` in AppSettings.IngestSelections; Preferences ids `Settings.IngestSelection.*`).
- **OS version**: `HostDiscovery.ReadOsVersion(root, opener?)` / `OsVersionText(softwareHive)` = "Windows 10 Pro 22H2 (build 19045.3803)" (Windows 11 derived from the build), null when unknown. Read after the scan, off the UI thread (`SystemChoiceItem.OsVersion`).
- **Detected artifacts** (below Scan, visible after a scan, `TriageViewModel.HasScan`): `Triage.Detected` with the cards `Triage.Detected.Disk` (`Triage.Detected.System` checkbox per system = `SystemChoiceItem.IsIncluded`, `Triage.Detected.NoSystem`, `Triage.Systems.Error`), `Triage.Detected.Memory` (`Triage.Detected.MemoryImage` = `MemoryImageItem.IsIncluded`, `Triage.MemoryImage.Notes`, `Triage.Detected.NoMemory`, `Triage.MemoryIncludePagefile`, `Triage.MemoryPlanning`, `Triage.MemoryIssue` + `Triage.SetUpVolatility`) and `Triage.Detected.Network` (`Triage.Detected.Pcap` = new `PcapChoiceItem.IsIncluded`, `TriageViewModel.Pcaps`; `Triage.Detected.NoNetwork`, `Triage.PcapTruncated`, `Triage.NetworkIssue` + `Triage.SetUpZeek`). An unticked capture is left out of the plan (`ActivePcaps`).
- **Removed**: the Detected systems block (`Triage.Systems`, `.Expander`, `.Summary`, `.Item.Check|HostName|Root|Acquisition|HostWarning`; host labels are no longer edited on the page), the scan summary line (`Triage.LayoutSummary`, "System root: …"), `Triage.Network`, `Triage.PcapCount`, `Triage.ZeekStatus`, `Triage.RitaStatus`, `Triage.NetworkDetails`, `Triage.MemoryCard`, `Triage.MemorySummary`, `Triage.VolatilityStatus`, `Triage.MemoryOfflineNote`, `Triage.Persistence`, `Triage.PersistenceSummary`, `Triage.PersistenceStatus`, `Triage.Details`, `Triage.MemoryImages`, `Triage.MemoryImage*` (except `.Notes`), `Triage.Notes`; the Case folder reset button and `ResetCaseFolderCommand`; VM `LayoutSummary`, `NetworkSummary`, `SystemsSummary`, `Notes`, `ZeekStatusText`, `ZeekShortStatus`, `NetworkDetails`, `Rita{Status,SubnetsText,ShortCaptureNote,ShortStatus}`, `ShowSetUpRita`, `SetUpRitaCommand`, `OpenToolsConfigCommand`, `Volatility{StatusText,ShortStatus}`, `MemoryDetails`, `MemorySymbolsText`, `MemorySummary`, `SetUpVolatilityCommand`, `Persistence{Summary,StatusText,ShortStatus}`. `Triage.ScanSummary` is now only the archived evidence (`Triage.ArchiveCard`, `Triage.EvidenceArchives`).
- **Messages**: hints only for issues. Run problems and the run's completion show once, in `Triage.RunStatus` / `Triage.RunHint` (no banner copy); a clean start or a cancelled run shows nothing; with the super timeline ticked the one completion message is written after it ("Ingest finished: … Super timeline built: …"). `RunHint` shows a disabled reason only when it must be fixed (system name error, empty case folder; Warning); the others stay in the Run button tooltip. The banner keeps scan problems (no source, nothing found, archive warnings, errors); removed: archive extracted / SHA copied / extracted copy deleted successes, the container-archives info and the Zeek / Volatility not-set-up banners (now on their card).

## Ingest availability and detail window (Oct 2026)

- **Greyed rows** (`Core/Triage/ToolAvailability`): `RowReason(planned, ToolStatus, locator, preferred)` = why a planned row cannot run (null = available): EZ tool not installed (`TriageRunCheck.ToolMissing`), Zeek, RITA (needs Zeek too), Volatility, plaso (super timeline); computer details and the built-in persistence row never. `ToolStatus` flags are false while a tool is still being probed. `PlannedJobItem.UnavailableReason` / `IsAvailable` / `RowOpacity` / `RowToolTip`: the row is dimmed, its checkbox disabled and unticked (ticked again by default when the tool appears); Defaults and All skip it. `TriageViewModel.RefreshAvailability()` runs after every plan change and tool change.
- **Greyed cards**: `ToolAvailability.Card(kind, status, hasItems, ezAllMissing)` -> `CardState` (Disabled, Reason, Note, SetUpText, Target). Disk: all EZ rows missing -> "Set up" (setup wizard, `Triage.SetUpDisk`); Memory: Volatility -> "Set up Volatility" (`Triage.SetUpVolatility`); Network: Zeek -> "Set up Zeek", RITA only -> note + "Set up RITA" (not greyed) (`Triage.SetUpZeek`, `SetUpNetworkCommand`). Replaces the "not set up" warning lines; removed `Triage.MemoryIssue`, `Triage.NetworkIssue`, VM `MemoryIssue`, `NetworkIssue`, `SetUpZeekCommand`. A greyed card still opens its details.
- **Detail window**: clicking a Detected artifacts card (not a checkbox, link or scroll bar) opens `IngestDetailWindow` (`Ingest.Detail.Window`, `Ingest.Detail.Box.<n>`, `Ingest.Detail.Close`): one bordered box per system / capture / image with **Key:** value lines in a shared two-column grid. Rows from `Core/Triage/ArtifactDetails` (Disk: Host, OS, Build, System root, Users found, Evidence size when from an archive; Memory: File, Size, Modified, Detected OS; Network: File, Size, Format).

## Setup wizard round 6 (Oct 2026, cancel / revert, resume in the wizard)

Supersedes the earlier sections where they disagree.

- **Skip removed**: `Setup.Skip`, `SkipCommand`, `ShowSkip`, `Skip()` are gone. New footer button `Setup.Cancel` (`CancelSetupCommand`, visible = `ShowCancel`: not on Done; on a later wizard only while nothing runs). The Install page's own `Setup.Install.Cancel` (`CancelInstallCommand`) shows only on later wizards (`ShowStopInstall`).
- **First setup** (`SetupWizardViewModel.FirstSetup`: opened by `SetupLauncher.OpenAlone()`, or `OpenResume(state)` with `state.FirstSetup`): Cancel or the X (any page but Done) asks `SetupWizardViewModel.CancelQuestion` ("Cancel setup? Tools installed so far will be removed.", Yes/No, `SetupHost.ConfirmCancel`); Yes stops the run, reverts, clears `SetupResume` + RunOnce, sets `SetupWizardSeen = false`, closes with `SetupWizardResult.Cancelled` and the app exits (no main window). Later wizards: Cancel just closes (`Closed`).
- **Core/Setup/SetupRevert.cs**: `SetupRevertPaths` (`Current(settings)`), `SetupSnapshot.Take(paths, ezDistroExists)` (taken once before the first step), `SetupRevert.Targets(steps, paths, snapshot)` -> `List<RevertTarget>` (`RevertKind.Path | EmptyFolder | WslDistro`), `Merge`, `RunAsync(targets, dataRoot, removeDistro, log)`. A step counts when `SetupRevert.Ran` (Done / Failed / Cancelled / ActionNeeded / Running; never AlreadyInstalled, Skipped, Queued, WaitingForRestart, BuiltIn); only paths new since the snapshot and under the data root; EZ tools = new top-level entries of the tools root / `net9`; the distro only when it did not exist (`EzDistro.RemoveAsync` on the maintenance queue). EnableWsl, SyncMaps, Zeek / RITA inside an existing distro and settings are never reverted.
- **SetupResumeState**: new `FirstSetup` (bool) and `Installed` (`List<RevertTarget>`, what the first setup installed before the restart). `SetupHost.RecordOutcome(s, plan, firstSetup, installed)` fills them and no longer sets `SetupWizardSeen`.
- **Resume after the restart**: `StartupMode.ResumeInBackground` -> `StartupMode.ResumeWizard`. `App` opens only the wizard (`SetupLauncher.OpenResume`), which starts on the Install page, plans the pending items (hidden distro item kept), runs them (`ResumeAction.Run`), or shows Done (`Finished`; `RestartWindows` = Done with Restart now and RunOnce registered again). The main window opens after it closes. Single-instance mutex and `WslWelcomeGuard` unchanged.
- **Removed**: `SetupResumeViewModel`, `ResumeBannerState`, the `Shell.SetupResume*` banner, `ShellViewModel.SetupResume` / `ResumeSetupAsync`, `AppServices.SetupResumeBusy` / `SetupResumeChanged` / `SetupResumeActive` (the first-run hint is now `!HasAnyEzTools`), `SetupLauncher.Closed`, the `resume` parameter of `SetupLauncher.Open`.
- **Profiles**: `SetupItem.OptIn` = never Recommended or a bundle tick; Everything selects it (the offline symbol pack `volatility3-symbols`).
- **Installer**: `build/make-splash.ps1 -Out <png> [-Version]` draws the 400x250 splash; `make-installer.ps1` passes `--splashImage` and `--splashProgressColor #4C9AFF` to `vpk pack` (without a splash Velopack 1.2.161 Setup.exe shows an OK-button "please wait" progress dialog).

## Setup wizard W7 (Oct 2026, resume list and live install order)

- **Resume shows only pending steps**: `SetupWizardPolicy.PendingOnly(plan)` returns the plan without `BuiltIn` steps and steps already succeeded at plan time (`AlreadyInstalled`). `SetupWizardViewModel.BuildPlan` applies it when the wizard was opened with a resume, so `Steps`, `Plan.Steps`, the runner, `AfterRunAsync` and the Done page see only the pending items' steps (plus a step they still need, e.g. `enable-wsl`). `ResumeAction` is unchanged (empty -> `Finished`, admin step left -> `RestartWindows`). Revert data (`_priorInstalled`, snapshot targets) is unchanged: dropped steps never ran.
- **Live order**: `Core/Setup/SetupStepOrder` (`Reset()` after `Steps` gets a new plan, `Update()` after a state change, `Phase(state)` 0 running / 1 queued / 2 finished). Order = running, queued in plan sequence, finished in finish order (newest last; steps finished at plan time count first, in plan order; a retried step goes back to the queue). Only `ObservableCollection.Move`. Called from `OnStepChanged`, on the UI dispatcher. May later be replaced by a shared run-order helper.
- **No auto-scroll**: `SetupWizardWindow` `CenterStep` / `OnVmChanged` / `FindScrollViewer` removed; `RunningStep` stays on the VM. `Log_TextChanged` still scrolls the log to its end.
## Ingest live order and run lock (round 7, Oct 2026)

Supersedes the earlier sections where they disagree.

- **Run order** (`Core/Common/RunOrder.cs`): `enum RunPhase { Running, Queued, Finished, Inactive }`; `RunOrder.Sort(items, phase, sequence, finishedAt)` = running, queued (by sequence), finished (by finishedAt, newest last), inactive (by sequence), stable; `Apply(ObservableCollection, order)` / `Reorder(...)` move items with `Move` only (never rebuild); `NextStamp()` = strictly increasing, time-based finish stamp. Used by the Ingest planned jobs and the Jobs page (the wizard install list is next, W7).
- **Planned jobs order**: `PlannedJobItem.Phase` (running; waiting / unfinished job = queued; finished or `NotQueuedReason` = finished; else ticked = queued, unticked = inactive), `Sequence` (plan order), `FinishedStamp`. `TriageViewModel.ReorderNow` / `ScheduleReorder` (dispatcher, Background) re-apply it when a row's `IsSelected`, `Status`, `ExcludedReason` or `UnavailableReason` changes. The run still queues in plan order (`OrderBy(Sequence)`). Header sorting is off (`CanUserSortColumns="False"`) on `Triage.Planned` and `Jobs.Grid`.
- **Card ticks exclude, never remove** (`Core/Triage/IngestExclusion.cs`): the plan is built from every artifact (`SystemSelection.Label`, all captures, all memory images, all persistence roots). `IngestExclusion.Reasons(plan, IngestCardSelection(systems, excludedMemoryImages, excludedPcaps))` per row: memory rows follow only their image (`MemoryReason` "Memory not selected"), Zeek rows only their capture (`NetworkReason`), RITA rows their host's included captures (case-wide: at least two), disk / persistence rows their system (`DiskReason` "Disk not selected", with several systems "Disk not selected: <host>"), computer details and the super timeline are excluded when no Disk system is ticked. `PlannedJobItem.ExcludedReason` / `IsExcluded` / `CanToggle` / `WantedSelected` / `SetWanted(bool)`: an excluded row is greyed (`RowOpacity` 0.5), unticked, disabled, `RowToolTip` = the reason; its earlier tick comes back when the card item is ticked again. `MemoryImageItem.SelectedIds` no longer depends on `IsIncluded`. `SystemSelection.FilterPcaps` / `FilterMemoryImages` are no longer used by the page. Removed VM `ActiveFound`, `ActivePcaps`, `ActiveMemoryImages`, `ActivePersistenceRoots`, `IsRootIncluded`.
- **Run lock**: `TriageViewModel.IsRunLocked` (= `RunActive()`, refreshed with the run gate) / `CanEditPlan`. While locked: `SetPlan`, `ReplanKeepingTicks`, `ReplaceMemoryRows`, the memory background plan, `RefreshPersistencePlan`, `RefreshRitaRows`, `RefreshSupertimelineRow` and Scan refuse (logged); a scan that finishes during a run is not applied (banner). Disabled: `ApplySelection`, `SelectAll`, `ClearSelection`, `BrowseSource`, `Scan`, `ExtractArchives` commands; the card checkboxes (`SystemChoiceItem.IsEditable`, `PcapChoiceItem.IsEditable`, `MemoryImageItem.IsEditable` / `CanToggle`, `PagefileEditable`; also false while the card's tool is missing) and the row checkboxes (`PlannedJobItem.IsLocked`). Row availability is not re-evaluated during a run; it is when the lock releases. Run results stay until the next Run or Scan.
- **Defaults** (`IngestDefaults.IsDefault`): only `FastForensicsRuleIds` (the disk set); memory, pcap and RITA rows are planned unticked. Defaults / All / Clear do not change the card checkboxes (an excluded row's remembered tick is set instead).
- **Jobs page**: `JobsViewModel.Rows` in the live order (`JobRow.Phase`, `JobRow.FinishedStamp`, sequence `Job.QueueSequence`). Removed the follow / auto-scroll: `JobsViewModel.FollowRequested`, `JobsView.Follow` and the scroll to the running job on Loaded. `ShowJob` / `ScrollRequested` (a job the user picks) stay.
- **Admin chip**: the *Admin* column is removed; the *Tool* column is a template (`Triage.Planned.Tool` text, then `Triage.Planned.AdminChip` "admin" chip when `RequiresAdmin`).
- **Card checkboxes**: no content; the label is a separate TextBlock (`Triage.Detected.System.Label`, `Triage.Detected.MemoryImage.Label`, `Triage.Detected.Pcap.Label`, plain "Include pagefile" text), so a click on the text opens the detail window and only the box toggles. Checkbox AutomationIds unchanged (`Triage.Detected.System`, `.MemoryImage`, `.Pcap`, `Triage.MemoryIncludePagefile`), with `AutomationProperties.Name` set.
- **Disk card lines**: per system `Triage.Detected.System.Type` ("Type: Folder | Mounted drive | Live drive | Archive", `ArtifactDetails.EvidenceType`) and `Triage.Detected.System.Artifacts` ("Artifacts: …", `ArtifactDetails.ShortList(groups, CardGroupLimit = 6)` = at most 6 then "+N more", "none" when empty; tooltip = full list). Groups = `ArtifactDetails.ArtifactGroups(plan, root)`: the EZ / persistence rows matched to that root, deduplicated in plan order, short names from `GroupName` (`$MFT`, `$UsnJrnl`, Registry, Event logs, Prefetch, Amcache, Shimcache, LNK, Jump lists, ShellBags, …; unknown rules: their label). `SystemChoiceItem.TypeText`, `ArtifactGroups`, `ArtifactsText`, `ArtifactsToolTip`. Detail window Disk box: new `Type` and `Artifacts` (full list) keys after System root (`ArtifactDetails.Disk(..., evidenceType, artifacts)`).
- **Run hint colours**: `Triage.RunStatus` uses DataTriggers on `RunHintSeverity` with `DynamicResource Brush.*` (follows theme switches). Removed `SeverityBrushConverter` and the `SevFg` / `SevBg` resource keys.

- **Jobs detail (command/log split)**: separate "Command" (`Jobs.Command`, `Jobs.CopyCommand`) and "Log" (`Jobs.Log`, `Jobs.CopyLog`) sections plus `Jobs.Meta` (start/finish/exit code) and `Jobs.Reason` (status bar: "Next step: ..." from `JobFailureAdvice.Advise(toolId, exitCode, reason, logTail)` for failed/timed-out jobs, the raw reason otherwise; the Reason column keeps the raw error). The log header writes a multi-line command as `# Command: <line 1>` + `#| <line n>` continuations (`JobLogFormat`); `LogBuffer` drops header lines (`JobLogFormat.IsHeaderLine`) so the Log shows process output only.

- **Events search clear (u10)**: the "Clear" button (`Results.ClearFilters`) is gone. `Results.Search.Clear` (X, tooltip "Clear search and filters (Esc)") overlays the `Results.Search` box and runs `ClearFiltersCommand`; visible while `ResultsViewModel.HasAnyFilter` (search text, column/builder filters or flag filter). Esc in the search box does the same. "Open any CSV…" (`BrowseCsvCommand`) is removed; the empty state reads "Pick a CSV from the tree.". The `Shell.Gear.Tools` item opens Tools (no `Nav.Tools`).

- **Case tree horizontal scroll (u10)**: the TreeView shows its own horizontal scroll bar when Name plus the visible columns are wider than the pane; `TreeHeaderScroll` follows its offset. The old snap-back to offset 0 is gone. A tree rebuild (load/refresh) resets both to 0; `RequestBringIntoView` on tree items (selection, keyboard) scrolls vertically only and keeps the horizontal offset.

## Per-host super timelines (p10)

- `IngestSteps.Supertimelines(caseDir, jobs, sources)` → one row per host folder that has disk rows (not memory / pcap / RITA / step rows): label `"Supertimeline (plaso): <Host>"` (`SupertimelineLabelFor`), `Spec.Args` = that system's root(s), `Spec.OutputDir` = `ArtifactOutputLayout.DirectoryFor(case, host, "Timeline")`, `Provenance.Host` = host folder, `Artifact.System` = the system (the row follows its Disk card tick in `IngestExclusion`). Disk rows with no system add one row over the sources holding no system root: host `_Case`, label "... unattributed evidence". `Compose` appends them last (only with plaso); all unticked by default. `SupertimelineHost(p)`, `HostDisplay(folder)`, `UnattributedHostLabel`. `IngestSteps.Supertimeline(caseDir, sources)` is gone.
- App: `_runTimelines` (every ticked timeline row) start after every other run job, `BuildSupertimelinesAsync` runs them one after the other (`PlasoRunOptions.OutputDir` / `Provenance` from the row), `IsBuildingTimeline` stays true until the last one ends; one completion message from `Core/Plaso/SupertimelineSummary.Describe(IReadOnlyList<SupertimelineOutcome>)` ("Super timelines built for 2 hosts (WS01 12,345 rows; DC01 678 rows)." + each failure). `RefreshSupertimelineRow` adds / removes all timeline rows (restored session ticks applied by row key).
- Analyze: no timeline is moved into `_Case` any more. `CaseTreeOrder.LegacyTimelineHost(hosts)`: an old case-level `<case>\Timeline` is shown under the only host, else stays at the top. `_Case` shows as `CaseTreeOrder.CaseWideDisplayName` "Case-wide (unattributed)", tooltip "Results whose source host could not be identified".

## Analyze menus and Hot Keys (r16)

- Core/Settings `HotKeys` (static): `Actions` (id, name, group, default: `Analyze.ViewValue` Enter, `Analyze.FilterContains` Ctrl+Shift+F, `Analyze.FilterNotContains` Ctrl+Shift+X, `Analyze.FilterEquals` Ctrl+Shift+E, `Analyze.ClearFilters` Ctrl+Shift+L, `Analyze.CopyRow` Ctrl+Shift+C, `Analyze.ExportFiltered` Ctrl+Shift+S, `Analyze.ShowInExplorer` Ctrl+Shift+O, `App.Back` "Go back" group Navigation Alt+Left), `Fixed` (read-only: Space/Ctrl+F2, Ctrl+0..9, Ctrl+C, Esc, F11), `TryParse/Format/Normalize`, `Resolve(overrides)`, `ToOverrides`, `Conflicts`, `FindConflict`, `ActionFor`. `AppSettings.HotKeys` = overrides by action id (missing = default, "" = unassigned); a refused conflict keeps the old chord.
- App: `ResultsViewModel.HotKeyMap` follows `AppSettings.HotKeys` live; `EventsPane` handles the chords (not while a text box has focus; cell actions need the grid focused). `HotKeyInput` turns key events into gesture text.
- Cell menu groups (caption items): View, Filter (Contains, Does not contain, sub-menu `Results.Menu.MoreFilters` with Equals/NotEquals/BeginsWith/EndsWith/IsEmpty/IsNotEmpty), Copy (CopyCell, CopyRow, sub-menu `Results.Menu.MoreCopy` with CopySelection/CopyColumn), Flag, Clear. All `Results.Menu.<id>` ids unchanged. Captions: `Results.Menu.Caption.<Name>`; Actions captions `Results.Actions.Caption.<Name>`.
- Actions menu: new item `Results.Actions.Shortcuts` (Keyboard shortcuts dialog).
- Options > Hot Keys card: `Settings.HotKeys.<actionId>.Key|Clear|Reset|Warning`, `Settings.HotKeys.ResetAll`, `Settings.HotKeys.List`, `Settings.HotKeys.Fixed`, HelpTip `Settings.Help.HotKeys`.
- Themes/Controls.xaml: `MenuIcon`, `MenuIconInfo|Accent|Warn|Error|Success`, `MenuCaptionItem`.

## Back navigation (R16)
- `Controls/PageHeader` (page title + Back button) heads Ingest, Jobs, Analyze, Tools and Options. Button AutomationId `Page.Back` (glyph E72B), always visible, disabled when `ShellViewModel.CanGoBack` is false; tooltip `BackTip` ("Back to <Title> (<gesture>)").
- `ShellViewModel`: back stack of `NavItem` (max 30, consecutive duplicates skipped, Back itself does not push), `GoBackCommand`. `MainWindow` also goes back on Alt+Left (customizable action `App.Back`, applied live) and the mouse back button (XButton1); the key is ignored while a text box has focus.
- `AppSettings.LastPage` = `NavItem.Key` (page view-model type name); saved on every navigation, the app reopens on it (unknown or null = Ingest). Never opens the setup wizard; not copied by `SettingsViewModel.Copy`.

## Saved filters and search syntax (Analyze)
- Data file `%LOCALAPPDATA%\EzForensicsData\saved-filters.json` (`SavedFilterStore`, version 1, atomic write; a missing, corrupt or newer-version file reads as empty). Categories are keyed by `ColumnLayoutStore.Signature(headers)` (one per CSV column layout) with a display name (`ArtifactCategoryNamer`, renamable) and the column list; filters (`SavedFilter`: Name unique per category case-insensitively, Expression = `AnalyzeFilterNode`, GlobalText, SavedUtc) belong to a category. `AnalyzeFileState.SavedFilterName` remembers the applied filter per file in the Analyze session.
- App: `Services/SavedFilters.Instance` (lazy load, save after each change, `Changed` event); `ResultsViewModel` partial `SavedFilters` (`CurrentSignature`, `CurrentCategoryName`, `ActiveSavedFilter`, `SaveFilter`, `SaveFilterAs`, `ApplySavedFilter`, `ApplyBuilderResult`, `OpenAndApplySavedFilterAsync`); a missing column on apply gives the warning "Filter 'X': columns not in this CSV were ignored: A, B".
- Search box (`SearchQuery`): words are ANDed, `OR` / `NOT` (UPPERCASE only), `( )` groups, `"exact phrase"` (`"OR"` = the literal text). An invalid query shows a red border + tooltip (`ResultsViewModel.SearchError`) and the grid keeps the last valid result; `FilterEngine.FilterAsync` throws `ArgumentException` for unparsable `GlobalText`. Tree content-search keywords are pre-filled as a quoted phrase when they contain spaces.
- AutomationIds: top bar `Results.EditFilter` is gone; `Results.SearchHelp` is now the circled "?" toggle inside the search box (right edge, shown only while the search text is empty; hover opens, click pins; Esc / click-away / clicking it again closes), `Results.SearchHelp.Popup`, `Results.SearchHelp.Example.1..7`, `Results.Search.Error`. Filter panel: `Results.FilterPanel.Actions` (+ `.Actions.Menu`), `.Edit`, `.Save`, `.SaveAs`, `.Manage` (opens the builder on the Saved filters tab), `.ClearAll` (last item, disabled without a filter), `.SavedName`; `.ApplySaved*` is removed. File tree menu (sections Open / Edit / Folder and, for one selected CSV, Filters): `Results.Tree.Menu.Filter` (submenu with `.Item.N`, `.None`, `.Error` only) plus sibling items `.Builder` ("Open filter builder": opens the file, then the builder on the Builder tab) and `.Manage` ("Manage saved filters": opens the file, then the builder on the Saved filters tab); `Results.Tree.Menu.SavedFilter*` and `.Add` are removed. Filter builder: `FilterBuilder.Tabs`, `.Tab.Builder`, `.Tab.Saved`, `.SavedName`, `.Notice`, `.Save`, `.SaveAs`, `.Saved.List`, `.Saved.Category`, `.Saved.Row` (double-click loads), icon buttons `.Saved.Rename|Delete` (filter) and `.Saved.RenameCategory|DeleteCategory`; removed: `.SearchText`, `.SearchError`, `.Saved.Load`, `.Saved.Duplicate` and the Saved-tab start option (`startOnSavedTab`).
- Filter builder: no search box; Save / Save as store the top-bar search text (a loaded saved filter brings its own text, OK applies it to the search box). Save as proposes the next free `Filter N`; when the name exists, Replace / Cancel, and Cancel returns to the name prompt (`SavedFilterUi.PromptSaveName`). **Any field**: `FilterCondition.Column == "*"` (`FilterNode.AnyColumn`, shown `Contains(*, 'x')`, picker entry "Any field", the default for new conditions) is true when any cell passes a positive operator, and for Not Contains / `<>` / IsEmpty when no cell passes Contains / `=` / IsNotEmpty; it never goes to a filter-row cell (stays in the builder-only part) and `SavedFilterStore.Columns` and the missing-column warning skip it. Prompts and secondary windows show no application icon (`Services/WindowChrome.HideIcon`, `EzDialog` always plain).
- `CsvIndex.ReadHeaderNames(path)` is public (header-only read with the grid's parser rules; used by the tree menu, cached per path + size + write time).
- **Search box X (round 17)**: `Results.Search.Clear` is now a circled X (`ClearCircleButton`, tooltip "Clear search") in the search box's right slot, shown only while search text is present (`HasSearchText`; swaps with the `?`). It runs `ClearSearchCommand`, clearing only `GlobalText` (other filters stay). Esc in the box still clears search and filters.

## Analyze pivot (grouping)
- Core: `Results/PivotEngine.cs` (`PivotSort`, `PivotGroupSpec`, `PivotNode` with `Key` = escaped `col=val/col2=val2`, `PivotLine`, `GroupAsync`, `Flatten`). App: `ResultsViewModel.Pivot.cs` (`GroupColumns` of `PivotGroupChip`, `IsGrouped`, `AddGroupColumn(column, index)`, `MoveGroupColumn`, `RemoveGroupColumnCommand`, `ToggleGroupSortCommand` (chip click: `PivotSort.Flip()` = same key, opposite direction), `SetGroupSortKey(chip, byCount)` (chip menu; `WithKey` keeps the direction; `PivotSortExtensions` in Core), `ClearGroupingCommand`, `ExpandAllGroupsCommand`, `CollapseAllGroupsCommand`, `ToggleGroupAsync`, `FilterToGroup` = Equals per path level via the filter-row cell or `Extra`, blank → IsEmpty).
- Pipeline: `ApplyFilterAsync` filters + sorts, then (grouped) `PivotEngine.GroupAsync` + `Flatten` off the UI thread under the same `_filterCts` / progress bar; a chip change regroups the last filter result without re-filtering; collapse / expand only re-flattens. Structural chip changes (add / remove / move) clear the collapsed set and start **all groups collapsed** (`PivotEngine.AllKeys`, applied once the tree is built: top level collapsed, expanding a group shows its subgroups collapsed); a sort flip keeps the set. Restoring a session / view uses its stored collapsed set, or all collapsed when none is stored (`ResultsViewModel.SeedCollapsed`).
- `VirtualRowList.ShowGrouped(map, lines, groupOrder)`: items are `RowItem` (data, `Source` = CSV row, `Position` = line index) or `GroupRowItem` (header line: `Node`, `Depth`, `Text` "Col: value (Count = N)", `Indent` 16 px per level). Code that maps a visible line to a CSV row must use `as RowItem` / `OfType<RowItem>()` (selection, flags, copy, cell menus, viewer, session restore already do). `CurrentIndices()` returns every data row in group order (collapsed groups included) for Export / Copy filtered.
- View: header lines replace the `DataGridRow` template (row style trigger on `IsGroupHeader`); data rows are indented inside the flag column (width 40 + 16 per level). Dragging a column header starts a group drag (the built-in column reorder is cancelled) as soon as the pointer leaves the header row vertically, enters the group panel from any direction or leaves the grid sideways; plain horizontal drags inside the header row stay a column reorder. Grouped columns are collapsed in the grid (`EventsPane._pivotHidden` remembers whether the user had hidden them; ungrouping restores that visibility, position and width) and are never saved as hidden in column layouts or saved views. Chips: a click flips the group order (▲ ascending / ▼ descending, `#` prefix for count order), drag reorders, dropping a chip on the grid ungroups it; right-click a chip for Sort by value / Sort by count / Ungroup. Keys on a focused header line: Left collapse, Right expand, Enter toggle, Up / Down step to the next line.
- Session: `AnalyzeFileState.GroupColumns` / `.CollapsedGroups` per file; status bar shows `N groups · M rows` (top-level groups).
- AutomationIds: `Results.Pivot.Panel`, `.Hint`, `Results.Pivot.Chip.<Column>` (the `.Remove` / `.Sort` buttons were removed), panel buttons right-aligned while grouped `Results.Pivot.ExpandAll` (Full Expand) / `.CollapseAll` (Full Collapse) / `.Clear` (Clear Grouping), chip menu `Results.Pivot.ChipMenu.SortByValue|SortByCount|Ungroup`, panel menu `Results.Pivot.Menu.ExpandAll` (Full Expand) / `.CollapseAll` (Full Collapse) / `.Clear` (Clear Grouping), `Results.Pivot.Header` (group lines; `.Header.Toggle` = chevron), group line menu `Results.Pivot.Menu.ExpandAll|CollapseAll|FilterToGroup|CopyValue|Remove`, column header menu section Group: `Results.Header.Menu.GroupBy` (disabled when already grouped) and `Results.Header.Menu.Ungroup` (grouped columns only).

- **Views (saved column layouts)**: data file `%LOCALAPPDATA%\EzForensicsData\saved-views.json` (`SavedViewStore`, version 1, atomic write, corrupt/newer reads as empty). Same categories as saved filters (`ColumnLayoutStore.Signature(headers)`, `SavedFilterCategory`). `SavedView` = Name (unique per category, case-insensitive), CategorySignature, Columns (`SavedViewColumn` Name/Visible/Width?, in display order), optional Groups (`PivotGroupSpec`, null = leave grouping as is) + CollapsedGroups, SavedUtc. Next default name "View N" (one above the highest). Applying matches columns by name case-insensitively; missing columns are skipped with a warning toast, unlisted columns keep their state. `AnalyzeFileState.SavedViewName` remembers the applied view per file. App: `Services/SavedViews.Instance`, `ResultsViewModel.SavedViews.cs` (`CaptureView`, `SaveView`, `SaveViewAs`, `ApplySavedView`, `ApplyViewCore`, `OpenAndApplySavedViewAsync`), grid state reached through `IGridColumnBridge` (implemented by `EventsPane`).
- AutomationIds: Actions menu caption `Results.Actions.Caption.Views`, `Results.Actions.View.Save|SaveAs|List|Manage|Clear`, items `Results.Actions.View.Item.<n>` (none row `Results.Actions.View.List.None`); tree menu `Results.Tree.Menu.Views`, `Results.Tree.Menu.Views.Item.<n>`, `Results.Tree.Menu.Views.None`, `Results.Tree.Menu.Views.Manage`; name prompt check box `Results.Prompt.Check` ("Include grouping (Pivot)", only while grouped). View builder window `ViewBuilder` (`ViewBuilder.Tabs`, `.Tab.Builder|Saved`, `.Columns`, `.Column.<name>`, `.MoveUp|MoveDown|ShowAll|HideAll`, `.Group.Choice|Add`, `.Group.<col>.Sort|Remove`, `.Save|SaveAs|Cancel|Ok`, `.Summary`, `.Notice`, `.Saved.List|Category|Row|Rename|Delete|RenameCategory|DeleteCategory`, `.Saved.Edit` = "Edit in builder" button beside Rename / Delete, same as double-click; `.Saved.Import|Export`, `.Apply`). Column header right-click menu (sections View and Clear): `Results.Header.Menu.SaveView|SaveViewAs|ManageViews|ClearView`. *Clear view* (also `Results.Actions.View.Clear`) forgets `ActiveSavedView` and restores all columns visible, original file order and default widths; filters are untouched and grouping is removed only if the active view applied it; enabled when a view is active or the columns differ from the default (`ResultsViewModel.CanClearView`).

- **Filter syntax box and Edit in builder**: `FilterSyntax.TryParse(text, out node, out error)` (Core) parses exactly the text `FilterNode.ToDisplayString()` prints (groups And/Or/Not, every `ColumnFilterMode`, `In (...)`, `InDays(...)` with `#d#..#d#` ranges, `*`, `'..'` with `''` escapes; keywords case-insensitive); empty text is a valid empty filter, and a list shortened for display (`...(+N)`) is rejected. Round trip: `TryParse(n.ToDisplayString())` renders the same text. Filter builder: the bottom box under the Builder tab is an editable monospace text box (`FilterBuilder.Syntax`, builder tree only, not the search text) with a Copy button (`FilterBuilder.Syntax.Copy`); typing/pasting is debounced 300 ms, valid text rebuilds the tree, invalid text keeps the builder, turns the border red and shows `FilterBuilder.Syntax.Error` ("Invalid syntax: ..."). While the box has focus builder changes do not overwrite it. `FilterBuilder.Preview` is replaced by `FilterBuilder.Syntax`. Saved filters tab: `FilterBuilder.Saved.Edit` pencil at the end of the syntax line of each filter (same as double-click; disabled for other categories).
- **Builders are modeless; footer; Import / Export** (filter builder `FilterBuilderWindow`, view builder `ViewBuilderWindow`): both open with `Show()` owned by the main window (stay above it, the Analyze grid stays usable; closing the main window closes them) and are single-instance (`ViewHelpers.EditFilter`, `SavedViewUi.Edit`): opening again activates the open window and selects the requested tab (Builder, or Saved with `savedTab`). Builder tab footer: `.Save`, `.SaveAs` (left group), then `.Apply` (applies to the current file, stays open; `FilterBuilder.Apply` / `ViewBuilder.Apply`), `.Ok` (apply + close; stays open if the apply failed, error shown via `ShowError("Invalid filter" | "Could not apply the view")`), `.Cancel` (closes without applying; Esc does the same). The footer sits below the syntax box / summary and shares its left and right edges. Saved tab footer: only `.Ok`, which reads **Close**, plus left-aligned `.Saved.Import` / `.Saved.Export` (`FilterBuilder.Saved.*`, `ViewBuilder.Saved.*`). Re-targeting: the window listens to `ResultsViewModel` Headers / Rows / CurrentFile (150-200 ms debounce); the filter builder refreshes columns and category, keeps its tree and shows `FilterBuilder.Notice` "Columns not in this CSV (ignored when applied): ..." for missing columns; the view builder takes the new file's columns, grouping and category as its draft when the column layout differs. Without a CSV Apply / OK are disabled with the tooltip "Open a CSV first" (Close still works). The search-box text is only written by Apply / OK after a saved filter was loaded in the builder (otherwise the box keeps what the user typed meanwhile). Saved views rows no longer show the "N of M columns" line nor a "Saved '<name>' to ..." notice; `ViewBuilder.Saved.Edit` sits beside Rename / Delete on the name line.
- **Exchange files**: `*.ezfilters.json` = `{ version, kind: "ezforensics-saved-filters", categories: [...], filters: [...] }` (`SavedFilterLibrary` shape) and `*.ezviews.json` = `{ version, kind: "ezforensics-saved-views", categories, views }`. Core: `SavedFilterStore.Export(lib, filters, path)` / `SavedViewStore.Export(lib, views, path)` write only the categories the chosen items use; `TryImport(path, out lib, out error)` rejects a wrong `kind`, a version above 1 (or below 1) and unreadable / corrupt JSON with a readable message. `SavedFilterLibrary.Merge(incoming, selected, ConflictMode)` / `SavedViewLibrary.Merge(...)` return `MergeResult(Added, Replaced, Skipped)`; `ConflictMode` = `Replace` | `KeepBoth` ("Name (2)", "(3)" ...) | `Skip`; categories missing locally are added with their name and columns, existing local category names are kept. App: `SavedFilterUi.Export/Import`, `SavedViewUi.Export/Import` (picker, then file dialog, notice "Exported N filters" / "Imported N, replaced R, skipped S"; the save dialog proposes `saved-filters.ezfilters.json` / `saved-views.ezviews.json`).
- **Saved item picker** (`Views/Analyze/SavedItemPickerWindow`, modal, used by both Import and Export): items grouped by category with check boxes (all selected by default), tri-state **Select all** and tri-state category headers, "N selected", OK disabled when none selected; for import also "If a name already exists:" Replace / Keep both (default) / Skip. AutomationIds `SavedPicker.List`, `.Category`, `.Item`, `.SelectAll`, `.Count`, `.Conflict` (+ `.Conflict.Replace|KeepBoth|Skip`), `.Ok`, `.Cancel`.

## Analyze process tree (item 3, branch feature/process-tree)
- Core: `ProcessTreeLayout.Compute<T>(roots, children, nodeW, nodeH, hGap, vGap, rootGap)` -> `TreeLayoutResult<T>` (Positions, Edges, Width, Height), pure left-to-right tidy layout. `ProcessGraphBuilder.IsProcessListCsv(path)`, `ProcessGraphFiles.HasProcessLists`, `ProcessFacetIndex.FilesFor(kind, pid)` -> (Path, PidColumn), `ProcessRowLocator.FindRowsAsync(csv, pid, name, createTime, ct)`.
- App: `Controls/ProcessGraphCanvas` (Source = `IProcessGraphSource`, NodeTemplate, two-way `Zoom`, `Fit`, `ZoomBy`), `ProcessTreeViewModel` / `ProcessNodeViewModel` / `ProcessDetailViewModel` (rail = `RailTabs`, one per `ProcessTab`: Overview, Timeline, Network, Files, Registry, Modules, Threats, Scripts ("Scripts & console"), Identity, Persistence, Execution, Sources; a new memory facet goes in `ProcessActivity.MemorySets` and `ProcessFacetIndex`), `Views/Analyze/ProcessTreePane.xaml`.
- Hosting (round 4): the Processes nav page, `ProcessesViewModel`, `ProcessesView`, `Nav.Processes`, `Processes.HostPicker|Empty` and Core `ProcessHostCatalog` are removed. The tree is the top half of Analyze's right pane: `ResultsView.xaml` right column is a 3-row grid (`ProcessTreePane` with DataContext `ResultsViewModel.ProcessTree`, horizontal `GridSplitter` `Results.ProcessTree.Splitter`, `EventsPane`). Top row and splitter are 0 high while `ResultsViewModel.IsProcessTreeOpen` is false; default split 55/45 star, the dragged split is remembered for the app session. `ResultsViewModel.OpenProcessTreeAsync(hostFolder)` opens the split (same host: reused; other host: old tree disposed) and returns the loaded `ProcessTreeViewModel` (null if closed meanwhile); `OpenProcessTree(host)` is the fire-and-forget form; `CloseProcessTree()` releases it. Opening another CSV keeps the tree open. A missing / stale host output is correlated on open (`CorrelateProcessesAsync`). `ResultsViewModel.HostFolderOf` maps a `Memory` folder to its host.
- Entry points (all open the split through `ResultsViewModel.OpenProcessTree(host)`): case tree host / Memory folder context menu item `Results.Tree.Menu.ProcessTree` ("Process" group, enabled by `ResultsViewModel.CanShowProcessTree`); host context menu `Results.Tree.Menu.RebuildProcessCorrelation` (label "Rebuild process tree data", AutomationId unchanged; queues the forced correlator job and reloads an open tree of that host when it succeeds). The pane toolbar shows the host (`Results.ProcessTree.Title`) and a Close (x) button `Results.ProcessTree.Close` (tooltip "Close the process tree"; replaces `Results.ProcessTree.Back` "Open in Analyze"); the card's "Open rows in grid" only loads the bottom grid (no navigation); "+ Add to Evidence" flags rows through Analyze's flag store. After an Ingest run has reported its outcome, `TriageViewModel.QueueProcessTreeData` queues a background "Process tree data: HOST" job for each host of `IngestSteps.ProcessHosts` that has a process source and is not up to date (not counted in the run message; Ingest Stop cancels them while queued / running).
- Auto-close (round 8): opening a CSV in the grid with no PID links calls `CloseProcessTree()` (`ResultsViewModel.UpdatePidLinks` -> `HasPidLinks(PidLinkHost, headers)` = host with process data and Core `PidLinks.HasPidSource(headers)`: a `IsPidColumn` header, or a `PidLinks.IsPidTokenColumn` one: EvtxECmd `PayloadData1`-`PayloadData6` and `ExecutableInfo`, the only columns the grid token-scans for "ProcessId: 0x…"; other text columns stay plain, so registry / prefetch CSVs close the graph). A CSV with PID links never auto-opens the graph and leaves an open one open.
- Clickable PIDs (round 4): only when the open CSV has a host with process data (`ResultsViewModel.PidLinkHost` = Core `PidLinks.HostFolderOfCsv(caseRoot, csv)`: the top folder under the case root; null for a CSV in the case root (super timeline), under `_Case` or a dot folder; then `CanShowProcessTree`). Then PID columns (`PidLinks.IsPidColumn`) get the link look (accent text, hand cursor, underline on hover, left-aligned) when the cell parses as a PID (`PidLinks.TryParsePid`: 0 / blank never, PID 4 yes), and other text columns get Run + `Hyperlink` inlines for `PidLinks.FindTokens` matches ("NewProcessId: 0x1a2c"); a cell without tokens stays one plain string (`Views/Analyze/PidLinkCells`: attached `ColumnText` / `TokenText` replace the column's Text binding, `ClipboardContentBinding` keeps Ctrl+C). Token hyperlinks have `AutomationProperties.Name` "Open PID N in the process tree". A plain left click (no Ctrl / Shift / Alt, not a drag, not a double click) calls `ResultsViewModel.OpenProcessAtAsync(csvPath, dataRow, pid)`: row time = `PidLinks.RowTime` (first `TimeColumnIndex` header, parsed like the grid's dates, UTC), and for `Memory_*.csv` `PidLinks.RowCreateTime` (`CreateTime` cell); opens the split (`OpenProcessTreeAsync`, awaits load / correlation) and calls `ProcessTreeViewModel.SelectPid(pid, time, csv, createTime)` (`ProcessPidResolver.Resolve`): Found = `RevealAndSelect` (centred); Ambiguous = chooser strip `Results.ProcessTree.PidChooser` (Name "PID N matches K processes:") with one button `Results.ProcessTree.PidChooser.Item` per candidate (Name = its text "name · started yyyy-MM-dd HH:mm:ss UTC" or "started unknown", oldest first) and `Results.ProcessTree.PidChooser.Dismiss` (x); nothing is selected until one is picked; NotFound = `Results.ProcessTree.Status` "PID N is not in the process tree of HOST." and the graph is unchanged; a tree whose load failed is left alone. Details card row grids (`FacetGridColumns`): PID columns have the same link look; a click selects that process in the same tree (`SelectPidFromCard`: the row's `Time`, `Origin.CsvPath` and `CreateTime` cell).
- AutomationIds: `Results.ProcessTree.(Pane|Title|ShowExited|Fit|Back|Sources|Warnings|Status|Canvas|ZoomIn|ZoomSlider|ZoomOut)`, `Results.ProcessTree.Node.{Pid}(.Expand|.Collapse)`. Details card: `Results.ProcessTree.Detail.(Card|Collapse|Copy|Close|Name|Pid|Badges|TierChip|SourceChip|ParentLink|Markers|Field.{FieldId}|Field.{FieldId}.Copy|Rail|Rail.{Tab}|TabTitle|SubTabs|Tab.{RowSetId}|Grid|Note|Message|TabMessage|OpenInGrid)`, where `{Tab}` is the `ProcessTab` name of a rail tab (Overview, Network, Files, Registry, Modules, Identity; the card no longer shows Timeline, Threats, Scripts, Persistence, Execution, Sources, the "Linked rows" counts, "Show path" or "Add to Evidence") and `{RowSetId}` is the sub-tab's row set id.
- Non-memory sources: `ProcessGraphFiles.(EventLogs|Prefetch|CanBuildTree)` (found as `<host><Category>EventLogs*.csv` etc.; `CanBuildTree` only checks file existence), `ProcessGraph.Evidence` (`ProcessEvidence`: `Events` = `ProcessEventIndex`, `Disk` = `DiskEvidenceIndex`; `Get/GetStatus(kind, node)` for `ProcessFacetKind.Evt*` / `Disk*`). Security 4688 and Sysmon 1 rows merge into memory nodes by PID + creation time (+-2 s) or Sysmon GUID, unmatched ones become `FromLogsOnly` nodes. Hash: Sysmon 1 Hashes of that process only (Amcache is not process evidence). Memory Network / File handles / Registry handles facets fall back to the Sysmon 3 / 11 / 13 rows when memory has none.
- **Correlation rule (facts only, user decision 2026-10-25)**: `enum LinkTier { Exact, PathTime }`. Exact = the same Sysmon ProcessGuid, or the same PID with the row time inside the instance lifetime (start - 2 s .. exit + 2 s, else up to the next instance of that PID; `ProcessLifetimes.PidFits`). PathTime = the same full normalised path (`ProcessLifetimes.PathKeyOf`; a bare name has no key), a row time that fits the source's `TimeFit` (`start` with `toleranceSec [min, max]` in `sources.json`, or `during` = start <= t <= exit, else the evidence end, else before the next instance of the path) and exactly one instance of that path fitting (`ProcessLifetimes.IsUniquePathTime`; an instance with unknown start counts as fitting, so the match is ambiguous and not linked). There are no name-only, path-only or time-window links. Path sources: Prefetch (FilesLoaded entry ending with the executable, LastRun + PreviousRun0-7, start [-2, 15] s), Task Scheduler 200 / 201 (full action path, start), Defender 1116 / 1117 (process or detected file path, during). Not process evidence: Amcache, Shimcache, SRUM, MFT / USN, registry, persistence, LNK, jump lists, ActivitiesCache, Recycle Bin, RecentFileCache, 7045 / 4697 service installs, 4698 / 106 task registrations, AppLocker. Lookups: `ProcessEventIndex.Attach(nodes)` / `ProcessEvidenceIndex.Attach(nodes)` (the builder attaches `graph.AllNodes`) then `LinksFor(node, group?)` -> `ProcessEventLink(Event, Tier, Direction)`, `ProcessEvidenceIndex.Lookup/LookupAll(node)` -> `LinkedRow(Cells, Tier, SourceFile)`, `DiskEvidenceIndex.FindPrefetch(node)` -> `PrefetchMatch(Entry, Runs)`. Signature / Hash come only from Sysmon 7 / 1 of the same process.
- Add to Evidence flags the process's pslist (else psscan / pstree) row with the last used flag through `RowFlagStore` (the open grid's store when it is that file).

### Process correlator (Ingest)
- Core: `Processes/ProcessCorrelator.cs`. `ToolId` = `EzForensics.Processes`, `Category` = `Processes`, headless switch `--process-correlator --host <host folder> [--force]` (`App.xaml.cs` -> `RunCommandLine`; `Spec` builds the job for the Rebuild menu item). `RunAsync(hostFolder, force)` -> `ProcessCorrelationOutcome` (Built, UpToDate, NoSource, Failed, Cancelled).
- Output in `<case>\<Host>\Processes\`, written to temporary names and moved into place; `.inputs.json` is written last:
  - `Processes.csv`: one row per process instance. Columns: `Key, PID, PPID, ParentKey, Name, Path, CommandLine, User, CreateTime, ExitTime, Sources, Markers, Hash, HashSource, Signature, FileVersion, FirstSeen, LastSeen`, then `<Tab>Count` for Network, Files, Registry, Modules, Threats, Scripts, Identity, Persistence, Execution and Timeline, then `Guid, ParentGuid, ParentImage, Offset, SessionId, Wow64, Threads, Handles, Company, Product, VersionInfoSource, Role, RoleBasis, Indicators, IsGhost, Services, SetsFirstRow, SetsRowCount` (`Services`: JSON array of the svcscan names of the PID, memory nodes only; `SetsFirstRow` / `SetsRowCount`: the process's rows in Processes_Sets.csv, 0-based data row numbers; ghosts have none).
  - `Processes_Activity.csv`: one row per linked evidence row, Exact or PathTime only. Columns: `ProcessKey, PID, ProcessName, Time, Tab, What, Source, Tier, Summary, SourceFile, SourceRow, SetId, Cells` (`ProcessCorrelator.OriginOf` rebuilds the CSV and grid filter from `SourceFile` / `SourceRow`; `Time` has full precision `yyyy-MM-dd HH:mm:ss.fffffff`; `Cells` is the JSON array of the card grid cells, lined up with the set's `Headers`, each cell shortened with "…" only when the JSON would pass 30,000 characters). Rows are written per process, per tab, per set, in card order, and capped per set like the card (`ProcessActivity.MaxRows`).
  - `Processes_Sets.csv`: one row per (process, card row set), including sets without rows. Columns: `ProcessKey, Tab` (enum name), `SetId, Title, Status` (Available / NotFound / NoRows), `Message` (why no rows, with how to get the source), `SourceLabel, Remark, TotalRows` (real count, also when capped), `Headers` (JSON), `ActivityFirstRow, ActivityRowCount` (the set's rows in Processes_Activity.csv), `Rows`. Each process ends with its `sources` set (Tab Sources): `Rows` holds the Sources tab inline as JSON `[[category, source, status, rows for this process, how to get it], ...]`.
  - `.inputs.json` (camelCase): `version` (`FormatVersion` 3; older output is stale), `builtUtc`, `inputs` (`path`, `size`, `lastWriteUtcTicks` of every input CSV, sorted by path), `sourceCounts`, `warnings`, `nodes`, `activityRows`.
- Artifact ids `processes` (Processes.csv), `processes-activity` (Processes_Activity.csv) and `processes-sets` (Processes_Sets.csv), `OutputNaming` rename and identify rows (a collision number `_<n>` is allowed). `EvidenceKind.Processes` = category `Processes`; the Analyze tree opens either CSV as a process tree.
- Ingest step (`IngestSteps.ProcessCorrelations`, rule id `ingest-process-correlation`): one ticked row per host that has a process source (a memory or EVTX row of the host in the plan, or process lists / event log CSVs already in its folder). Ranked last (rank 4, after the tool, MACB and super timeline rows); a built-in job, `IsStep` rows are replaced, not duplicated. Follows Stop / Resume like the MACB rows and is counted in the run report.
- Up-to-date skip: `RunAsync` returns UpToDate without rebuilding when `IsUpToDate` (the three CSVs exist, `.inputs.json` version matches and the input list is the same by path, size and last write time). `--force` (Rebuild process correlation) always rebuilds. No process source -> NoSource (no files).
- One correlation path: only the correlator correlates (`ProcessCorrelation` / `ProcessActivity` are its Core engine; the App never builds them). The Analyze process tree reads the files through `ProcessActivityStore.OpenAsync(host)` (null when missing or stale): the graph from Processes.csv, and the details card (`IProcessDetailSource`: `SetsFor`, `Timeline`, `Sources`, `Counts`, `SignatureText`, `ServiceNames`) from Processes_Sets.csv / Processes_Activity.csv through `CsvIndex` row offsets; a process's sets and rows are read only when its card asks (last 8 processes' sets cached), so large activity files stay on disk. Missing or stale: the tree shows "Correlating processes…" and `ResultsViewModel.CorrelateProcessesAsync` queues the correlator job (not forced; an unfinished correlator job of the same host is awaited instead), then opens the new files; closing the tree stops the wait, not the job. Rebuild process tree data (forced job) reloads the open tree when it succeeds.

### Process tree round 3 (Navigator, stacks, ghosts, Focus)
- Facts only: stacks and Focus are view constructs over the real PPID edges; no edge is ever invented. Ghost parents (`ProcessNode.IsGhost`, `ProcessGraph.Ghosts`) are not counted, searched or added to evidence; a selected ghost shows the one-line ghost card instead of the details card (no Trace actions).
- Core: `ProcessNavigatorGroups.Build(graph)` (Navigator sections); `SiblingStacks.Group(siblings, name, pinned, expanded?, threshold = 3)` -> `SiblingItem<T>` (single or stack): same-name siblings (case-insensitive) under one real parent or at root level stack when >= 3 are visible; pinned ones are never stacked and a stack needs >= 2 unpinned members; empty names (ghosts) never stack. `SiblingStacks.StackContaining`. `FocusProjection.Compute(node, parent, children, roots, FocusScope)` -> `FocusProjection<T>` (`Ancestors`, `Others` per level, `Breadcrumb`, `Items`, `IncludesSubtree`, `ExpandsSubtree`); `FocusScope` = Focus (ancestors + node + subtree as expanded), TraceUp (ancestors + node), TraceDown (node + subtree fully expanded), Lineage (both, fully expanded). `ProcessFacetIndex.ServiceNames(pid, cap)` / `PidsWithServices()`, `ProcessActivity.ServiceNames(node, cap)`.
- App: `IProcessGraphSource.LayoutRoots()` / `LayoutChildren(item)` feed `ProcessTreeLayout` (unchanged) with `ProcessNodeViewModel`, `ProcessStackViewModel` (stack; while expanded it is the "Collapse stack" pill in front of its members; state per parent + lower-case name). `ProcessGraphCanvas` templates: `NodeTemplate`, `StackTemplate`; edges from a ghost parent are dashed. Pinned (never stacked): flagged, with an indicator, Navigator group member, search match, and every ancestor of those and of the selected process (the selected process itself is not pinned: clicking a stack member must not split the stack). A reveal (Navigator member, Parent Process link) expands the stack that holds the target. Modes `ProcessCanvasMode` Tree | Focus (`ShowTreeCommand` / `ShowFocusCommand`, Focus disabled with "Select a process first" when nothing is selected); entering Focus saves the Tree expansion and leaving restores it. Card actions `TraceUpCommand` / `TraceDownCommand` / `LineageCommand`. Services chip on a node ("3 services", tooltip lists up to 20 names) from svcscan, read off the UI thread whenever the activity updates.
- AutomationIds: `Results.ProcessTree.Navigator`, `.Navigator.Toggle`, `.Navigator.Clear`, `.Navigator.More.{Section}.{Key}`, `.Navigator.Section.{Section}`, `.Navigator.Empty.{Section}`, `.Navigator.Group.{Section}.{Key}`, `.Navigator.Member.{Pid}`; `Results.ProcessTree.Detail.GhostCard`, `.Detail.GhostNote`, `.Detail.Field.Role`, `.Detail.Field.Indicator{n}`, `.Detail.Field.{Id}.More` ("more…"/"less" link under a value longer than 160 chars or multi-line, clamped to 3 lines until expanded; Copy still copies the full value); `Results.ProcessTree.Mode.Tree`, `.Mode.Focus`, `.Breadcrumb`, `.Breadcrumb.{Pid}`, `.Stack.{ParentPid}.{name}` (`{ParentPid}` = `Root` at root level, `{name}` lower case), `.Detail.TraceUp`, `.Detail.TraceDown`, `.Detail.Lineage`, `.Legend`, `.Node.{Pid}.Services`, `.Node.{Pid}.Exited` (grey stop marker of an exited process; nodes are never faded).
- Path view and card rows (round 5): a PID click (`SelectPid` Found, chooser button) (the card's "Show path" button was removed) call `ProcessTreeViewModel.OpenPath(node)` = `EnterFocus(node, TraceUp)` + centre. The Trace up node starts collapsed with its expand pill (`Toggle` expands one level in place, staying in Focus); its descendants and `IsDrawn` siblings count as drawn, so selecting them does not leave Focus. The "+N others" pill is gone (Oct 27): every drawn chain ancestor starts collapsed and draws only the next chain item; its normal "N children" expand pill (counts all its visible children) shows all its children in their real order (same-name stacks apply; the chain child keeps its slot), and a collapse goes back to the chain child only. Descendants of a drawn chain ancestor count as `IsDrawn`. No pill in Focus, Trace down or Lineage either. Expand / collapse layout (`ProcessTreeLayout.Update`, Oct 27): "move only on overlap, push down": a depth-first walk gives every item a minimum row (the bottom of every finished subtree + one row step, + `RootGap` before a later root; a new first child no higher than its parent); a drawn item keeps its x and takes max(old row, minimum), a new one the minimum. An expanded subtree pushes everything below it down, nothing moves up, a collapse leaves gaps; `Compute` runs only when `IProcessGraphSource.LayoutGeneration` changes. `ShowWholeTreeCommand` / `CanShowWholeTree` (= `IsFocusMode`) leave Focus centred on the selection. Navigator and the Parent link keep `RevealAndSelect`. Round 8 simplification (the graph only shows the processes): no search (`SearchText`, matches, Ctrl+F / Esc), no breadcrumb (`Breadcrumb`, `ProcessCrumbViewModel`), no Tree | Focus toggles, Expand / Collapse all, Only flagged or legend, and no Trace down / Lineage commands (Core `FocusProjection` keeps all scopes). The ghost node tooltip carries "Dashed line: the parent process was not captured by any source".
- Card Overview rows (`Results.ProcessTree.Detail.Field.{Id}`; `ProcessDetailField.Hint` is the tooltip of an empty row): always `StartTime`, `Path`, `CmdLine`, `Parent`, `Sources`; only when present `EndTime` (the exit time, else "No exit time (in the active process list at capture)" for a pslist node, else omitted), `User` (`DOMAIN\user (UserSource)`), `Integrity`, `Role`, `Signature`, `Hash` (`SHA256=... (Sysmon 1)`), `Memory` (`Session N · WoW64 (32-bit) · offset X`), `Activity` (`first → last`, one time when equal), `Indicator{n}`. Removed ids: `ProcessName`, `Pid`, `FileVersion`, `HashSource`, `UserName`, `Domain`, `FirstSeen`, `LastSeen`. Copy details lists the same rows. Tests: `tests/EzForensics.App.Tests` (App view models; `ProcessTreeViewModel.ShowGraphAsync` is internal for them).
- Floating controls (round 6): the canvas fills the pane; there is no toolbar card, Sources line, status bar or fixed Navigator column. Controls float over the canvas as glass chips (`GlassChip` style: Brush.Surface layer at 0.92 opacity with a soft shadow under opaque content; 28 px round `GlassIconButton` / `GlassIconToggle`). Top-left host chip `Results.ProcessTree.HostChip` (warning icon `Results.ProcessTree.Warnings`, title `Results.ProcessTree.Title`; its tooltip is the sources summary `Results.ProcessTree.Sources`). The host chip shows only the host name and the warnings icon; in the path view the pill `Results.ProcessTree.ShowWholeTree` sits right of it (`ShowWholeTreeCommand`, visible when `CanShowWholeTree`). (Round 10: the top-right dock and its fold-to-gear are gone; the View menu, Navigator toggle and Close sit on the left rail, see below.) The Navigator is a 260 px floating panel on the left under the host chip, with a close button `Results.ProcessTree.Navigator.Close`. The PID chooser is a floating card under the host chip, at most 60 % of the pane wide. The bottom bar `Results.ProcessTree.ZoomBar` (round 11; was the left rail) is one horizontal glass bar centred at the bottom of the canvas (10 px up), in this order: `Results.ProcessTree.ViewMenu` (24 px toggle; its popover opens above it), a separator, `ZoomOut`, a 110 px horizontal `ZoomSlider`, `ZoomIn` (the percentage is in their tooltips), a separator, `Navigator.Toggle`, `Close`; it does not move when the Navigator opens. `ProcessGraphCanvas.FitBottomInset` (44 in the pane) keeps that strip free when Fit centres the graph. View popover items (round 10): `Fit` "Fit to screen" (`FitCommand`), `ViewMenu.ShowWholeTree` "Show whole tree" (visible when `CanShowWholeTree`), checkbox `ShowExited` "Show exited processes", checkbox `ShowGrid` "Show grid" (`ProcessTreeViewModel.ShowGrid`, default on, kept for the app session like the Navigator's open state; bound to `ProcessGraphCanvas.ShowGrid`, off collapses the floor); each label is followed by a `c:HelpTip` with its one-line explanation (no caption lines); clicking a button item closes the popover. The bottom-centre toast `Results.ProcessTree.Toast` (54 px up, above the bottom bar) (with `Results.ProcessTree.Status`, not hit-testable) shows while `IsLoading` and for 4 s after `StatusText` changes to non-empty, then fades to opacity 0 (it stays in the automation tree). The detail card is 400 px wide by default; its height fits its content up to the pane height minus 32 (MaxHeight follows the pane), the resize grips clamp to that, and it sits 10 px from the right edge (as does the ghost note card). The "Click a process to see its details." hint sits bottom-right.
- Isometric floor (round 7, replaces the round 6 slanted plane and its `Slant` toggle): every layout point (x = depth, y = row, from `ProcessTreeLayout` with `LayoutNodeWidth` 165 + `HorizontalGap` 33 = 198 per column, `LayoutNodeHeight` 150 + `VerticalGap` 36 = 186 per row, `RootGap` 18; Oct 27: round 10 values x1.5 with the node presenter, now 210 x 72 with the diamond centre at (24,27), `ConnectorRadius` 15, `GridCell` 93) is the item's diamond centre and is projected by x' = (x − y)·cos θ, y' = (x + y)·sin θ with θ = `ProcessGraphCanvas.ElevationDegrees` = 20 (round 10: a lower camera than the 30° isometric of round 7; grid lines run 20° from horizontal) (`ProcessGraphCanvas.Project`, `IsoX` / `IsoY`): a child steps right-and-down from its parent, a sibling left-and-down. Presenters (`NodeWidth` 140 × `NodeHeight` 48) are not transformed; Canvas.Left / Top put their diamond centre (`DiamondCenterX` 16, `DiamondCenterY` 18) on the projected point. The pitch keeps name pills apart: an item's box is about x −13..+118, y −14..+27 around its anchor, and two items with layout delta (dx, dy) clear when |(dx + dy)·sin θ| ≥ 41 or |(dx − dy)·cos θ| ≥ 131 (at 20°: |dx + dy| ≥ 119.9 or |dx − dy| ≥ 139.4; a sibling 124 rows apart clears vertically, a child 132 to the right clears for any row offset since 2·132 ≥ 259.3). Edges are straight lines between the projected diamond centres (solid Brush.BorderStrong; dashed Brush.TextDisabled from a ghost parent). Canvas coordinates are the projected points (the first root at 0,0, so nodes above an expansion stay put); Fit and CenterOn use the projected bounds, which can start at a negative x. Floor: a Rectangle behind the canvas filling the viewport, Fill Brush.Border at opacity 0.4, masked by a tiled DrawingBrush (one projected layout cell of `GridCell` 62 (round 10; half a row step) with its two diagonals, so lines run along both projected layout axes); its Viewport follows the zoom / pan on every view change (a tile corner on the projected origin; the cell doubles while a rhombus is under 18 px high; the pen is rescaled to stay 0.7 px). The canvas background stays Brush.Input. Node template: 22 px diamond (gear) on a pedestal ellipse with a radial-fade shadow; a 7 px severity dot at the diamond's top-right (error / orange / warn / info, none without a severity marker); the name pill to the right (11 px SemiBold, padding 6,1, corner radius 8, name at most 90 px); under it the "N children" pill (`.Expand`), the 15 px round collapse button (`.Collapse`) and the services count chip (`.Services`). The PID and the badge texts are in the node tooltip (`ToolTipText`: "name · PID n", command line or path, one line per badge). Stacks (two diamonds + "name ×N" pill, exited count under it) and Focus "+N others" (dashed hollow diamond + pill) use the same size and anchor. Test: `ProcessTreePaneSmokeTests` (the pane parses, lays out and binds without binding errors; a child's presenter is right-and-below its parent, a sibling's left-and-below).
- Navigator tree (round 9): the Navigator is a `TreeView` with the shared guide-line item style `GuideTreeItem` (Themes/TreeStyles.xaml, merged in App.xaml; the Analyze case tree's `CaseTreeItem` is `BasedOn` it and adds only its automation properties and filter visibility). Rows are `NavigatorTreeItem` (`IsExpanded`, `IsSelected`, `IsFirst`, `IsLast`, `IsTopLevel`, `Children`): section (top level) -> groups (or one disabled `NavigatorNoteViewModel` with the empty text) -> members (+ a disabled "+ N more" note). A click on a row runs `Activate()` (`ProcessTreePane.NavItem_MouseDown`): group highlights / clears (selected group is expanded and selection-coloured), member reveals, section folds. The filter strip at the top shows while a group is selected: "Highlighting: {title}" and the `Navigator.Clear` x. The panel is resizable (right edge / bottom edge / corner grips; width 200+, kept for the app session).

- Round 10 (camera, rail, Navigator, role colours): lower camera (`ElevationDegrees` 20, pitch 132 × 124, `GridCell` 62) and View › Show grid, see the floor and floating-control entries above. Removed ids: `Results.ProcessTree.Dock`, `.Dock.Minimize`, `.Dock.Folded`, `.Dock.Expand` (and the pane's session fold state). New ids: `Results.ProcessTree.ShowGrid`, `Results.ProcessTree.Navigator.RoleSwatch.{RoleKey}`. Navigator: `SelectGroup` in the path view (Focus) first leaves it (`ExitFocus`) when any of the first `MaxRevealedMatches` members is not drawn (`IsDrawn`), then reveals the members and centres the first one; in Tree mode the view does not move. Group members are pinned (never stacked), so no stack is opened for them. A row click runs `Activate()` from `NavTree_MouseLeftButtonDown` (round 11: registered on the Navigator TreeView `NavTree` with handledEventsToo in the pane constructor; a TreeViewItem Style EventSetter was never called with real mouse input once the item's class handler had handled the press; the walk from the OriginalSource stops at the first TreeViewItem, or bails at the chevron ToggleButton, so only the innermost row acts; each click logs `Process tree: Navigator {section|group|member} '…' clicked`); the second press of a double-click is ignored. Role colours: each node's diamond is filled by its role (`ProcessNodeViewModel.RoleKey` = `RoleKeyOf(Node.Role)`: System, Session, Services, Security, Desktop, Shells, Admin, Remote, Office, Browsers, Other; `RoleText` and "Role: …" in the node tooltip) through the pane style `RoleFill` (DataTriggers to theme brushes `Brush.Role.{Key}`, defined in Themes/Light.xaml and Dark.xaml: slate blue, indigo, teal, green, sky, orange, magenta, red, blue, violet, grey); a ghost keeps its transparent dashed diamond; the outline still shows severity / group / selection, the gear is a light glyph, the severity dot stays on top and the exited / dimmed opacities are unchanged. Navigator Roles groups (`NavigatorGroupViewModel.RoleKey` / `HasRoleSwatch`) show the same colour as a small diamond before the title (the legend). roles.json: new role "Admin tools & LOLBins" (rundll32 and regsvr32 moved from Shells & scripts; certutil, bitsadmin, schtasks, at, wmic (system32\wbem), sc, reg, net, net1, msiexec, cmstp, forfiles with system32 / syswow64 dirs; installutil, regasm, regsvcs, msbuild with no dirs) and "Remote access" (mstsc, rdpclip, wsmprovhost, winrshost with system32 / syswow64 dirs; psexesvc, anydesk, teamviewer, screenconnect.clientservice with no dirs); none has parents, singleton or session0, so they raise no SuspiciousParent / DuplicateSingleton / UnexpectedSession. `ProcessCorrelator.FormatVersion` is 6 so older Processes.csv (old roles) count as stale. Tests: `ProcessTreePaneSmokeTests` (projection angle, Show grid, Navigator row presses, role brushes in both themes), `ProcessTreeViewModelTests` (Navigator groups from the path view), `ProcessRolesTests` (new roles, no indicators).
- Merged process list (round 11): besides the hidden backend files in `<host>\.ezprocesses` (Processes.csv, Processes_Activity.csv, Processes_Sets.csv, `.inputs.json`; never listed in the case tree), the correlator writes one visible, readable CSV `<host>\Processes\Processes.csv` (`ProcessCorrelator.ListPath`, `WriteList`; temp file + move; `FormatVersion` 7 so older hosts regenerate). One row per real process instance (no ghost rows), ordered by CreateTime (no CreateTime last, then PID). Columns `PID, PPID, Name, Path, CommandLine, User, Integrity, SessionId, Wow64, CreateTime, ExitTime, ParentName, Role, Indicators ("Kind: evidence; ..."), Sources ("pslist, psscan, pstree, Security 4688, Sysmon 1"), Hash, Signature, Company, Threads, Handles, Offset`; times `yyyy-MM-dd HH:mm:ss.fffffff` like the other outputs. The visible `Processes` folder name is reused: after a build `RemoveLegacyFolder` deletes the old correlator files in it (activity, sets, `.inputs.json`, `Processes_<n>.csv`, stats / flags sidecars) but never the new `Processes.csv`; a folder holding anything else is still left alone with a warning. Case tree: category `Processes` again (`CaseIndex.IsKnownCategory`, `EvidenceKinds.ForFolder("Processes")` = `EvidenceKind.Processes`, `ArtifactNaming` identifies `Processes(_n).csv` as `processes`). PID links: `PidLinks.HostFolderOfCsv` and `ResultsViewModel.HostFolderOf` map `<host>\Processes` to the host; `ProcessPidResolver.IsProcessListCsv` rows resolve by PID + CreateTime (to the second; blank CreateTime matches only instances without one; zero or several matches = Ambiguous, never a guess) and `OpenProcessAtAsync` passes the row's CreateTime for it like for a memory CSV.

- Process graph trail: `IProcessGraphSource.TrailNodes` (+ `TrailChanged`) = the real parent chain from the furthest ancestor the user reached (selected or revealed with "↑ N more") down to the PID that `OpenPath` was called for; it only grows upwards, resets on a new `OpenPath`, cleared when the path view is left. `ProcessGraphCanvas.BuildTrail` draws each trail child's own stub/spine/leg in `Brush.ProcessTree.Trail` (1.6x stroke) over the normal connectors; a change redraws connectors only (no layout, no view move).
- Process graph layout, collapse undoes expand: `ProcessTreeLayout.Update(..., baseRows)`; the canvas keeps each item's base row (its row in the last full `Compute`, or its first placement) and an item's row = max(base row, no-overlap minimum), so an expand (children or a same-name stack) pushes items down and the matching collapse puts them (legs and spines included) back on their original rows; nothing moves above its base row and the view never pans.
- Process graph semantic zoom (Google-Maps style, replaces the landmark labels and the far-zoom dot layer): the boot chain (`ProcessRegions.BootChain`: image name System / Registry / smss / csrss / wininit / winlogon / services / lsass / userinit / explorer, case-insensitive, whose parent is boot chain or that is a root; a nameless ghost root counts as its top only when one of its children has a boot name; with no such name drawn, depth <= 2) is always shown. Roots are laid out boot roots first, then every orphaned tree (root not boot chain) in one block a row step below (`ProcessTreeLayout` `groupStart` / `groupGap`, stable across expand / collapse). Every boot-chain parent gets one region, `Results.ProcessTree.Region.{ParentPid}`, around the drawn subtrees of its off-chain children; all orphaned trees share one, `Results.ProcessTree.Region.Orphaned`. A region lies on the ground plane: per run of adjacent subtrees a padded, rounded layout rectangle projected by the floor matrix (`ProcessGraphCanvas.ProjectionMatrix`), unioned, behind connectors and nodes; fill = dominant role colour, plain, no outline and no title or label (the `Results.ProcessTree.Region.*` id sits on the fill); < 2 processes: no region (orphans: from 1). Regions rebuild with a layout only.
- Zoom fade: a 0..1 far factor from the on-screen name size (fully far below ~6 px, i.e. zoom < ~0.36; close above ~10 px, zoom > ~0.6). Far: off-chain nodes and their connectors are hidden (not hit-testable from halfway), boot-chain nodes are inverse-scaled around their diamond (~70 % screen size, capped so no two boot pills overlap on screen, down to a 9 px name), connectors out of the boot chain keep >= 1 px (trail >= 2 px). Region fill is one plain role tint (opacity 0.05) at every zoom, with no outline and no title. At far / middle zoom a click on a region fill zooms to fit it; close in regions take no clicks. Zoom / pan only redo screen maths (no relayout, no LayoutGeneration bump).
- Landmarks, far zoom (revised): landmarks are only the top of the tree (drawn depth <= 3, the boot chain), ranked by depth then subtree size when labels overlap (`ProcessLandmarks.Cull`), so each zoom step brings the next depth. Connectors never fade (the stroke keeps >= 1 px, the trail >= 2 px, on screen); the whole-node layer fades out and a role-coloured diamond for every item (>= 6 px wide) fades in, so the tree shape stays readable.
